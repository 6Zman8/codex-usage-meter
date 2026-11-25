#!/usr/bin/env python3
"""Headless emulator check of the PC 리모컨 app against stub meters that follow docs/PHONE_APP.md.

It builds the debug app (update URL pointed at a local stub) and a copy with a higher versionCode,
boots the AVD without a window or audio, and then, through adb only:
  * shows the first-run screen, pairs two PCs with the codexmeter://pair deep link (allowing the
    Android 17 nearby devices prompt), and checks the name comes from GET /status ("PC" until then);
  * holds 화면 끄기 and 절전 against the stubs (and checks a short press sends nothing);
  * covers an unreachable PC, a renewed link (404) fixed by re-scanning, a rename the meter name must
    not overwrite, reordering and deleting, the remembered PC after a restart;
  * offers an update, refuses an APK whose SHA-256 does not match, then installs the verified one.
Screenshots land in <out>/screens. Everything it started (stubs, emulator, adb server) is stopped.

  py -3 android/tools/emulator_check.py [--avd codexremote-api37] [--out work/phone-app]

Needs JAVA_HOME and ANDROID_HOME. Exits non-zero at the first failed step.
"""

import argparse
import functools
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

PACKAGE = "io.github.sixzman8.codexremote"
UPDATE_PORT = 8766
HOST_FROM_EMULATOR = "10.0.2.2"
EMULATOR_PORT = 5580
SERIAL = f"emulator-{EMULATOR_PORT}"
ANDROID = Path(__file__).resolve().parent.parent

ANDROID_HOME = Path(os.environ["ANDROID_HOME"])
ADB = str(ANDROID_HOME / "platform-tools" / "adb")
EMULATOR = str(ANDROID_HOME / "emulator" / "emulator")


class CheckFailed(Exception):
    pass


FAILURE_SHOTS = None  # the output folder, once known, for a screenshot of the screen a step failed on


def log(text):
    print(time.strftime("%H:%M:%S"), text, flush=True)


# ---- Stub meter ----

class StubMeter:
    """One PC's meter: the same routes, header, token and status JSON as src/PhoneRemote.cs."""

    def __init__(self, pc_id, name, port, token):
        self.pc_id, self.name, self.port, self.token = pc_id, name, port, token
        self.requests = []
        self.served = []  # (time, method, resource, HTTP code) of every answer, for failure reports
        self.server = None

    def start(self):
        meter = self

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                self.route("GET")

            def do_POST(self):
                self.route("POST")

            def route(self, method):
                parts = self.path.split("?")[0].split("/")
                if len(parts) != 4 or parts[0] or parts[1] != "r" or parts[2] != meter.token:
                    return self.reply(404, "text/plain", b"not found")
                resource = parts[3]
                if resource not in ("status", "screen-off", "sleep"):
                    return self.reply(404, "text/plain", b"not found")
                if method != ("GET" if resource == "status" else "POST"):
                    return self.reply(405, "text/plain", b"method not allowed")
                if "Transfer-Encoding" in self.headers or self.headers.get("Content-Length", "0") != "0":
                    return self.reply(413, "text/plain", b"no body")
                if self.headers.get("X-Codex-Remote") != "1":
                    return self.reply(403, "text/plain", b"forbidden")
                meter.requests.append((method, resource))
                if resource == "status":
                    body = json.dumps({"status": "ok", "id": meter.pc_id, "name": meter.name, "version": "1.9.30"}, ensure_ascii=False)
                    return self.reply(200, "application/json; charset=utf-8", body.encode("utf-8"))
                self.reply(200, "application/json", b'{"status":"ok"}')

            def reply(self, code, kind, body):
                meter.served.append((time.strftime("%H:%M:%S"), self.command, self.path.split("/")[-1], code))
                self.send_response(code)
                self.send_header("Content-Type", kind)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *args):
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", self.port), Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        log(f"stub meter {self.name} on 127.0.0.1:{self.port}")

    def stop(self):
        if self.server:
            self.server.shutdown()
            self.server.server_close()
            self.server = None
            log(f"stub meter {self.name} stopped")

    def link(self):
        return f"codexmeter://pair?v=1&id={self.pc_id}&host={HOST_FROM_EMULATOR}&port={self.port}&token={self.token}"

    def count(self, method, resource):
        return self.requests.count((method, resource))


class UpdateServer:
    """Serves CodexMeterRemote.json and CodexMeterRemote.apk like the latest GitHub release download URLs."""

    def __init__(self, folder):
        self.folder = folder
        class Quiet(SimpleHTTPRequestHandler):
            # Keep-alive: with HTTP/1.0 the server closes right after writing and the emulator's NAT
            # sometimes drops the tail of the 14 MB APK, which looks like a failed download.
            protocol_version = "HTTP/1.1"

            def log_message(self, *args):
                pass

        handler = functools.partial(Quiet, directory=str(folder))
        self.server = ThreadingHTTPServer(("127.0.0.1", UPDATE_PORT), handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def publish(self, version_code, version_name, sha256):
        (self.folder / "CodexMeterRemote.json").write_text(
            json.dumps({"versionCode": version_code, "versionName": version_name, "sha256": sha256}), encoding="utf-8")

    def stop(self):
        self.server.shutdown()
        self.server.server_close()


# ---- Build ----

def gradle(project_dir, *tasks):
    wrapper = project_dir / ("gradlew.bat" if os.name == "nt" else "gradlew")
    log(f"gradle {' '.join(tasks)} in {project_dir}")
    subprocess.run([str(wrapper), "-p", str(project_dir), "--console=plain", "-q", *tasks], check=True)


def build(out):
    url = f"-PupdateBaseUrl=http://{HOST_FROM_EMULATOR}:{UPDATE_PORT}/"
    gradle(ANDROID, ":app:assembleDebug", url)
    apks = out / "apk"
    apks.mkdir(parents=True, exist_ok=True)
    v1 = apks / "remote-current.apk"
    shutil.copy2(ANDROID / "app/build/outputs/apk/debug/CodexMeterRemote-debug.apk", v1)
    props = dict(line.split("=", 1) for line in (ANDROID / "version.properties").read_text().splitlines()
                 if "=" in line and not line.startswith("#"))
    code = int(props["versionCode"].strip())
    # The same sources with the next versionCode, debug-signed with the same key, stand in for the next release.
    copy = Path(tempfile.mkdtemp(prefix="remote-next-"))
    try:
        shutil.copytree(ANDROID, copy / "android", ignore=shutil.ignore_patterns("build", ".gradle", ".kotlin", "local.properties"))
        (copy / "android/version.properties").write_text(f"versionCode={code + 1}\nversionName=1.1.0\n")
        gradle(copy / "android", ":app:assembleDebug", url)
        v2 = apks / "remote-next.apk"
        shutil.copy2(copy / "android/app/build/outputs/apk/debug/CodexMeterRemote-debug.apk", v2)
    finally:
        shutil.rmtree(copy, ignore_errors=True)
    return v1, v2, code


# ---- Device ----

def adb(*args, check=True, binary=False, timeout=120):
    result = subprocess.run([ADB, "-s", SERIAL, *args], capture_output=True, timeout=timeout)
    if check and result.returncode != 0:
        raise CheckFailed(f"adb {' '.join(args)} failed: {result.stderr.decode('utf-8', 'replace').strip()}")
    return result.stdout if binary else result.stdout.decode("utf-8", "replace")


def shell(command, check=True):
    return adb("shell", command, check=check)


def boot(avd, out):
    log(f"booting {avd} headless")
    emulator_log = open(out / "emulator.log", "wb")
    process = subprocess.Popen(
        [EMULATOR, "-avd", avd, "-port", str(EMULATOR_PORT), "-no-window", "-no-audio", "-no-boot-anim",
         "-no-snapshot", "-gpu", "swiftshader_indirect"],
        stdout=emulator_log, stderr=subprocess.STDOUT)
    deadline = time.time() + 600
    while time.time() < deadline:
        if process.poll() is not None:
            raise CheckFailed(f"emulator exited with {process.returncode}; see {out / 'emulator.log'}")
        state = subprocess.run([ADB, "-s", SERIAL, "shell", "getprop", "sys.boot_completed"], capture_output=True)
        if state.stdout.strip() == b"1":
            break
        time.sleep(3)
    else:
        raise CheckFailed("emulator did not finish booting in 10 minutes")
    # Animations off, like the "remove animations" setting: the 0.6 s hold must still take 0.6 s (the short press checks it).
    for setting in ("window_animation_scale", "transition_animation_scale", "animator_duration_scale"):
        shell(f"settings put global {setting} 0")
    shell("input keyevent KEYCODE_WAKEUP")
    shell("wm dismiss-keyguard", check=False)
    log("booted: Android " + shell("getprop ro.build.version.release").strip() + ", API " + shell("getprop ro.build.version.sdk").strip())
    return process, emulator_log


def screenshot(out, name):
    path = out / "screens" / f"{name}.png"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(adb("exec-out", "screencap", "-p", binary=True))
    log(f"screenshot {path.name}")


def nodes():
    for _ in range(5):
        dump = adb("shell", "uiautomator dump /sdcard/ui.xml", check=False)
        if "dumped to" in dump:
            break
        time.sleep(1)
    xml = adb("exec-out", "cat /sdcard/ui.xml", binary=True).decode("utf-8", "replace")
    result = []
    for node in ET.fromstring(xml).iter("node"):
        x1, y1, x2, y2 = map(int, re.findall(r"\d+", node.get("bounds", "[0,0][0,0]")))
        result.append({
            "text": node.get("text", ""), "desc": node.get("content-desc", ""), "id": node.get("resource-id", ""),
            "all": " ".join(t for t in (n.get("text", "") for n in node.iter("node")) if t),
            "class": node.get("class", ""), "package": node.get("package", ""), "selected": node.get("selected") == "true",
            "clickable": node.get("clickable") == "true", "center": ((x1 + x2) // 2, (y1 + y2) // 2),
        })
    return result


def fail(what):
    """Keeps a screenshot and the UI tree of the screen a step failed on, then fails."""
    if FAILURE_SHOTS:
        screenshot(FAILURE_SHOTS, "failure")
        (FAILURE_SHOTS / "failure-ui.xml").write_bytes(adb("exec-out", "cat /sdcard/ui.xml", binary=True))
        (FAILURE_SHOTS / "failure-logcat.txt").write_bytes(
            adb("logcat", "-d", "-s", "CodexRemote:V", "AndroidRuntime:E", "PackageInstaller:V", "System.err:W", binary=True))
    seen = [(n["package"], n["text"] or n["desc"]) for n in nodes() if n["text"] or n["desc"]]
    raise CheckFailed(f"{what}; screen: {seen}")


def find(text=None, desc=None, rid=None, cls=None, exact=False, timeout=15, gone=False):
    """Waits for a node whose text/description contains [text]/[desc] (or equals it when [exact]); with [gone], for its absence."""
    def match(n):
        if rid is not None and n["id"] != rid:
            return False
        if cls is not None and n["class"] != cls:
            return False
        for want, have in ((text, n["text"]), (desc, n["desc"])):
            if want is not None and (have != want if exact else want not in have):
                return False
        return True

    deadline = time.time() + timeout
    while True:
        found = [n for n in nodes() if match(n)]
        if bool(found) != gone:
            return found[0] if found else None
        if time.time() > deadline:
            fail(f"{'still showing' if gone else 'did not find'} text={text!r} desc={desc!r} id={rid!r}")
        time.sleep(1)


def texts():
    return [n["text"] for n in nodes() if n["text"]]


def tap(node):
    x, y = node["center"]
    shell(f"input tap {x} {y}")


def long_press(node, ms=900):
    x, y = node["center"]
    shell(f"input swipe {x} {y} {x} {y} {ms}")


def hold(node, seconds, out=None, shot=None, shot_after=0.7):
    """Presses and holds with real DOWN/UP events, optionally taking a screenshot mid-hold."""
    x, y = node["center"]
    shell(f"input motionevent DOWN {x} {y}")
    start = time.time()
    if shot:
        time.sleep(shot_after)
        screenshot(out, shot)
    time.sleep(max(0.0, seconds - (time.time() - start)))
    shell(f"input motionevent UP {x} {y}")


def deep_link(link):
    shell(f"am start -W -a android.intent.action.VIEW -d '{link}' {PACKAGE}")


def expect(condition, what):
    if not condition:
        fail("not true: " + what)
    log("ok: " + what)


def wait_until(condition, what, details=lambda: "", timeout=15):
    deadline = time.time() + timeout
    while not condition():
        if time.time() > deadline:
            fail(f"not true within {timeout} s: {what}; {details()}")
        time.sleep(1)
    log("ok: " + what)


def allow_permission_prompt():
    node = find(rid="com.android.permissioncontroller:id/permission_allow_button", timeout=15)
    tap(node)


LINK_WORDS = ("연결됨", "연결 안 됨", "확인 중")


def tabs():
    """The PC tabs: clickable nodes whose own texts are a PC name followed by its link state; "text" becomes that pair."""
    found = []
    for n in nodes():
        # A selected Compose tab drops its click action, so it is recognised by its selected state instead.
        if (n["clickable"] or n["selected"]) and any(n["all"].endswith(" " + word) for word in LINK_WORDS):
            found.append(dict(n, text=n["all"]))
    return found


def tab(name):
    found = [n for n in tabs() if n["text"].startswith(name + " ")]
    if not found:
        fail(f"no tab for {name!r}; tabs: {[n['text'] for n in tabs()]}")
    return found[0]


def count(word):
    return sum(t.count(word) for t in texts())


# ---- Scenario ----

def run(out, avd, skip_build):
    if skip_build:
        v1 = out / "apk/remote-current.apk"
        v2 = out / "apk/remote-next.apk"
        code = int(re.search(r"versionCode=(\d+)", (ANDROID / "version.properties").read_text()).group(1))
    else:
        v1, v2, code = build(out)
    name = re.search(r"versionName=(\S+)", (ANDROID / "version.properties").read_text()).group(1)

    updates = Path(tempfile.mkdtemp(prefix="remote-updates-"))
    shutil.copy2(v2, updates / "CodexMeterRemote.apk")
    v2_hash = hashlib.sha256(v2.read_bytes()).hexdigest()
    server = UpdateServer(updates)
    server.publish(code, name, hashlib.sha256(v1.read_bytes()).hexdigest())

    desktop = StubMeter("deskTop0001", "데스크톱", 43131, "tokenDesktop0000000001")
    laptop = StubMeter("lapTop00001", "노트북", 43132, "tokenLaptop00000000001")
    meters = [desktop, laptop]
    adb_was_running = os.name == "nt" and b"adb.exe" in subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq adb.exe"], capture_output=True).stdout
    emulator = emulator_log = None
    try:
        emulator, emulator_log = boot(avd, out)
        adb("uninstall", PACKAGE, check=False)
        adb("install", str(v1))
        expect(PACKAGE in shell(f"pm list packages {PACKAGE}"), "debug APK installed")

        # A · first run
        shell(f"am start -W -n {PACKAGE}/.MainActivity")
        find("아직 등록한 PC가 없습니다", timeout=60)  # a cold first boot can still be busy for a while
        find("QR 찍어서 PC 추가")
        screenshot(out, "A-first-run")

        # Pair the desktop through the deep link; Android 17 asks for nearby devices first.
        desktop.start()
        deep_link(desktop.link())
        allow_permission_prompt()
        expect("granted=true" in shell(f"dumpsys package {PACKAGE} | grep ACCESS_LOCAL_NETWORK"), "nearby devices (ACCESS_LOCAL_NETWORK) granted through the prompt")
        find("데스크톱")
        find("연결됨")
        expect(desktop.count("GET", "status") >= 1, "the app read GET /status with X-Codex-Remote: 1 and took the meter's name")

        # The laptop is paired while its meter is off: placeholder name, unreachable, buttons disabled.
        deep_link(laptop.link())
        find("새 PC를 등록했습니다.")
        find("PC에 연결할 수 없습니다.", timeout=20)
        expect("연결 안 됨" in tab("PC")["text"], "an unreachable new PC shows the placeholder name 'PC' and 연결 안 됨")
        expect(count("연결되면 누를 수 있습니다") == 2, "both buttons are disabled for the unreachable PC")
        find("다시 확인")
        find("QR 다시 찍기")
        screenshot(out, "D-unreachable-new-pc")

        laptop.start()
        find("노트북", timeout=20)
        find("연결 안 됨", gone=True, timeout=20)
        expect([n["text"].startswith(("데스크톱", "노트북")) and "연결됨" in n["text"] for n in tabs()] == [True, True],
               "the laptop's name arrives with its first status answer and both PCs are connected")

        # B · main screen with the desktop selected
        tap(tab("데스크톱"))
        time.sleep(1)
        expect(tab("데스크톱")["selected"], "tapping a tab selects that PC")
        screenshot(out, "B-main")

        # C · hold screen off: the gauge fills, then the request goes out
        off = find(desc="화면 끄기", exact=True)
        hold(off, 2.0, out, "C-holding", shot_after=0.3)
        find("데스크톱 화면을 껐습니다.")
        expect(desktop.count("POST", "screen-off") == 1, "holding 화면 끄기 past 0.6 s sent POST /screen-off once")
        screenshot(out, "C-done")

        # A short press drains the gauge and sends nothing.
        # One swipe in place keeps DOWN→UP at 0.25 s; separate `input` calls each take about a second to start.
        sleep_button = find(desc="절전", exact=True)
        long_press(sleep_button, 250)
        time.sleep(2.5)
        expect(desktop.count("POST", "sleep") == 0, "a 0.25 s press on 절전 sent nothing")

        # Sleep on the laptop.
        tap(tab("노트북"))
        time.sleep(1)
        hold(find(desc="절전", exact=True), 2.0)
        find("노트북이 절전에 들어갑니다.")
        expect(laptop.count("POST", "sleep") == 1 and desktop.count("POST", "sleep") == 0, "절전 went to the selected PC only")

        # D · the laptop goes away (asleep)
        laptop.stop()
        find("노트북에 연결할 수 없습니다.", timeout=20)
        expect("연결 안 됨" in tab("노트북")["text"] and "연결됨" in tab("데스크톱")["text"], "only the laptop tab shows 연결 안 됨")
        expect(count("연결되면 누를 수 있습니다") == 2, "its buttons are disabled")
        screenshot(out, "D-unreachable")
        # Tap right after the meter is back, before the next 4 s poll can reconnect it by itself.
        recheck = find("다시 확인")
        laptop.start()
        polled = laptop.count("GET", "status")
        tap(recheck)
        find("노트북에 연결할 수 없습니다.", gone=True, timeout=10)
        expect(laptop.count("GET", "status") > polled, "다시 확인 reconnects once the PC is back")

        # A renewed link answers 404 until the QR code is scanned again; same id updates, no duplicate.
        tap(tab("데스크톱"))
        desktop.stop()
        desktop.token = "tokenRenewed0000000002"
        desktop.start()
        find("연결 정보가 바뀌었습니다", timeout=20)
        screenshot(out, "D-link-renewed")
        deep_link(desktop.link())
        find("이미 등록된 PC라 주소만 고쳤습니다.")
        find("연결 정보가 바뀌었습니다", gone=True, timeout=15)
        wait_until(lambda: len(tabs()) == 2 and "연결됨" in tab("데스크톱")["text"],
                   "re-scanning the same PC id updated it to the new link instead of adding a second tab",
                   lambda: f"desktop stub answers: {desktop.served[-8:]}")

        # E · PC 관리
        tap(find(desc="PC 관리", exact=True))
        find(f"버전 {name}")
        find("확인 중", gone=True, timeout=20)
        if "업데이트를 확인하지 못했습니다" in texts():
            # The check at start ran before nearby devices access was allowed, and the stub release is on a
            # private address (10.0.2.2); the real one is on github.com. 업데이트 확인 must then succeed.
            causes = [line for line in adb("logcat", "-d", "-s", "CodexRemote:W").splitlines() if "Exception" in line or "manifest" in line]
            log("note: the start-up update check failed: " + (causes[1] if len(causes) > 1 else "".join(causes))[:220])
            tap(find("업데이트 확인", exact=True))
        find("최신 버전입니다", timeout=20)
        find(f"{HOST_FROM_EMULATOR} · 연결됨")
        screenshot(out, "E-manage")

        # Rename the laptop; the meter's own name must not come back.
        rename_buttons = [n for n in nodes() if n["text"] == "이름 바꾸기"]
        tap(rename_buttons[1])
        field = find(cls="android.widget.EditText", timeout=10)
        tap(field)
        shell("input keyevent KEYCODE_MOVE_END")
        for _ in range(6):
            shell("input keyevent KEYCODE_DEL")
        shell("input text Study")
        tap(find("저장", exact=True))
        find("Study")
        time.sleep(9)
        expect("Study" in texts() and "노트북" not in texts(), "the user's name survived two status polls that report '노트북'")

        # Reorder and delete through the long-press menu.
        long_press(find("Study", exact=True))
        tap(find("위로 옮기기"))
        time.sleep(1)
        order = [t for t in texts() if t in ("Study", "데스크톱")]
        expect(order == ["Study", "데스크톱"], "위로 옮기기 moved Study to the top")
        third = StubMeter("thirdPc0001", "서재", 43139, "tokenThird000000000001")
        deep_link(third.link())
        find("새 PC를 등록했습니다.")
        long_press(find("PC", exact=True))
        tap(find("삭제", exact=True))
        find("목록에서 지울까요?")
        tap(find("삭제", exact=True))
        find("PC", exact=True, gone=True)
        expect([t for t in texts() if t in ("Study", "데스크톱", "PC")] == ["Study", "데스크톱"], "삭제 removed the third PC and kept the others")

        # F · an update is offered; first a download whose hash does not match must never reach the installer.
        server.publish(code + 1, "1.1.0", "0" * 64)
        tap(find("업데이트 확인"))
        find("새 버전 1.1.0이 나왔습니다", timeout=20)
        shell("input keyevent KEYCODE_BACK")
        find("새 버전 1.1.0이 나왔습니다.")
        tap(tab("데스크톱"))
        time.sleep(1)
        screenshot(out, "F-update")
        tap(find("업데이트", exact=True))
        find("설치하지 않았습니다", timeout=60)
        expect("SHA-256 does not match" in adb("logcat", "-d", "-s", "CodexRemote:W"), "the APK downloaded fully and was rejected for its SHA-256")
        expect(PACKAGE in shell("dumpsys window | grep mCurrentFocus"), "a mismatched SHA-256 left the app in front; no installer was opened")
        screenshot(out, "F-update-rejected")

        server.publish(code + 1, "1.1.0", v2_hash)
        shell(f"appops set {PACKAGE} REQUEST_INSTALL_PACKAGES allow")
        tap(find("다시 시도", exact=True))
        confirm = find(rid="android:id/button1", timeout=60)
        expect("packageinstaller" in confirm["package"], "the verified APK opened the system installer")
        screenshot(out, "F-installer")
        tap(confirm)
        deadline = time.time() + 90
        while f"versionCode={code + 1}" not in shell(f"dumpsys package {PACKAGE} | grep versionCode"):
            if time.time() > deadline:
                raise CheckFailed("the update did not install")
            time.sleep(2)
        log(f"ok: versionCode {code + 1} installed over {code} by the in-app update")

        # The remembered PC and the list survive the update and a restart.
        shell(f"am force-stop {PACKAGE}")
        shell(f"am start -W -n {PACKAGE}/.MainActivity")
        find("Study")
        find("데스크톱")
        expect(tab("데스크톱")["selected"], "the last selected PC (데스크톱) is selected again after the update and restart")
        tap(tab("Study"))
        time.sleep(1)
        shell(f"am force-stop {PACKAGE}")
        shell(f"am start -W -n {PACKAGE}/.MainActivity")
        find("Study")
        expect(tab("Study")["selected"], "the last selected PC (Study) is remembered across restarts")
        log("ALL CHECKS PASSED")
    finally:
        for meter in meters:
            meter.stop()
        server.stop()
        shutil.rmtree(updates, ignore_errors=True)
        if emulator:
            subprocess.run([ADB, "-s", SERIAL, "emu", "kill"], capture_output=True)
            try:
                emulator.wait(timeout=60)
            except subprocess.TimeoutExpired:
                emulator.kill()
            emulator_log.close()
            log("emulator stopped")
        if not adb_was_running:
            subprocess.run([ADB, "kill-server"], capture_output=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--avd", default="codexremote-api37")
    parser.add_argument("--out", default=str(ANDROID.parent / "work" / "phone-app"))
    parser.add_argument("--skip-build", action="store_true", help="reuse <out>/apk from the previous run")
    args = parser.parse_args()
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    global FAILURE_SHOTS
    FAILURE_SHOTS = out
    try:
        run(out, args.avd, args.skip_build)
    except CheckFailed as failure:
        log(f"FAILED: {failure}")
        sys.exit(1)


if __name__ == "__main__":
    main()
