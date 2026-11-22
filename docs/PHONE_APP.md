# 폰 리모컨 앱 (Android)

같은 와이파이의 Android 폰에서 PC 여러 대(데스크톱·노트북)의 화면 끄기·절전을 꾹 눌러 실행하는 앱입니다.
화면 기준은 승인된 시안 `design/mockups/phone-app/index.html`입니다. Chrome 페이지는 없앴습니다
(최신 Android Chrome은 '근처 기기' 권한 없이 같은 와이파이 주소에 접속하지 못해 `ERR_TOO_MANY_RETRIES`가 납니다).

## 미터기와 앱 사이 규격 (v1)

### PC 등록 QR

```
codexmeter://pair?v=1&id=<pcId>&host=<IPv4>&port=<포트>&token=<토큰>
```

- 미터기의 QR 인코더는 버전 6(약 106바이트)까지라 이름은 QR에 넣지 않습니다.
- `id`: PC마다 고정된 무작위 11자(base64url, 레지스트리 `HKCU\Software\CodexUsageMeter\PhoneRemoteId`). 같은 `id`를 다시 찍으면 앱은 새로 추가하지 않고 주소·포트·토큰만 고칩니다(앱에서 바꾼 이름은 유지).
- 이름: 앱은 등록 직후 `/status`의 `name`(미터기 설정의 '앱에 보일 이름', 기본값은 Windows 컴퓨터 이름, `PhoneRemoteName`)을 받아 씁니다. 그때 연결되지 않으면 임시로 'PC'로 두고, 처음 연결될 때 미터기 이름으로 채웁니다. 사용자가 앱에서 이름을 바꾼 뒤에는 미터기 이름으로 덮어쓰지 않습니다.
- `token`: `PhoneRemoteToken`. '새 등록 QR 만들기'로 바꾸면 기존 폰 등록은 '연결 정보가 바뀌었습니다'가 되고 QR을 다시 찍어야 합니다.
- `host`: 미터기는 사설 주소(10/8, 172.16/12, 192.168/16)만 QR에 넣고, 앱도 이 범위(시험용 127/8 포함)가 아니면 등록하지 않습니다. 앱은 이 주소에만 암호화하지 않은 HTTP를 씁니다.
- 값은 모두 퍼센트 인코딩합니다. 앱은 `v`가 1이 아니면 '미터기를 업데이트하세요'로 안내합니다.

### HTTP API

모든 요청에 헤더 `X-Codex-Remote: 1`이 필요합니다. 요청 본문은 없습니다. 미터기는 다음 순서로 거절합니다.

1. 머리글이 8KB를 넘으면 400, `Transfer-Encoding`이나 0이 아닌 `Content-Length`가 있으면 413
2. 경로나 토큰이 틀리면 404
3. 메서드가 틀리면 405(`status`는 GET, 동작은 POST만)
4. 헤더가 없거나 값이 `1`이 아니면 403

| 요청 | 응답 |
|---|---|
| `GET /r/<token>/status` | 200 `{"status":"ok","id":"<pcId>","name":"<이름>","version":"<미터기 버전>"}` (UTF-8 JSON) |
| `POST /r/<token>/screen-off` | 200 `{"status":"ok"}` 후 PC 화면을 끕니다 |
| `POST /r/<token>/sleep` | 200 `{"status":"ok"}` 후 PC가 절전에 들어갑니다 |

기본 포트는 TCP 43130이며 방화벽 규칙은 미터기 프로그램·이 포트·개인 네트워크로만 열립니다.

## 배포

- 앱 버전: `android/version.properties`의 `versionCode`(정수, 올릴 때마다 +1)와 `versionName`.
- 미터기 Release(`v*` 태그)마다 같은 Release에 두 파일을 함께 올립니다.
  - `CodexMeterRemote.apk`: 서명된 설치 파일
  - `CodexMeterRemote.json`: `{"versionCode":N,"versionName":"x.y.z","sha256":"<apk SHA-256 소문자 hex>"}`
- 설치 링크(미터기 설정의 '앱 설치 QR'): `https://github.com/6Zman8/codex-usage-meter/releases/latest/download/CodexMeterRemote.apk`
- 앱은 열릴 때 `.../releases/latest/download/CodexMeterRemote.json`을 읽어 `versionCode`가 더 크면 업데이트를 알리고, 받은 APK의 SHA-256을 확인한 뒤 Android 설치 화면을 띄웁니다.
- 서명 키는 GitHub 비밀값(`ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`)과 프로젝트의 Git 제외 폴더 `secrets/android/`(키 파일 `codexremote-release.jks`, 별칭·비밀번호 `signing.properties`)에 보관합니다. GitHub 비밀값은 다시 읽을 수 없으므로 로컬 사본이 원본입니다. 키가 바뀌면 기존 앱 위에 업데이트할 수 없습니다.
- 인증서 SHA-256: `B0:12:B8:C8:90:E2:6A:EC:30:DA:51:AE:A8:1E:73:29:DB:59:2A:5D:90:67:14:7C:7A:5F:C8:77:36:3D:27:FD`. 배포 워크플로는 APK 서명이 이 인증서가 아니면 게시를 멈춥니다.
- 이 PC에서 정식 서명 APK를 직접 빌드하려면 `ANDROID_KEYSTORE_PATH`를 `secrets/android/codexremote-release.jks`로, `ANDROID_KEYSTORE_PASSWORD`·`ANDROID_KEY_PASSWORD`를 `signing.properties`의 `password`로, `ANDROID_KEY_ALIAS`를 `alias`로 두고 프로젝트 폴더에서 `android\gradlew.bat -p android releaseAssets`를 실행합니다(결과는 `android/app/build/release-assets/`). CI는 `ANDROID_KEYSTORE_BASE64`를 임시 파일로 풀어 같은 변수를 채웁니다. 디버그 빌드(`assembleDebug`)는 키가 필요 없습니다.
- 앱 코드를 바꿔 배포할 때는 `android/version.properties`의 `versionCode`를 1 올리고 `versionName`을 바꿉니다. 그대로 두면 폰 앱은 새 버전으로 알리지 않습니다.
- CI(windows-latest)는 기본 제공 JDK 17(`JAVA_HOME_17_X64`)과 Android SDK에 `platforms;android-37.2`, `build-tools;36.0.0`을 설치해 `android` 폴더 안에서 `.\gradlew.bat --no-daemon testDebugUnitTest releaseAssets`를 실행합니다.
- Google은 Play 밖에서 받은 앱의 개발자 인증을 2027년 1월부터 전 세계로 넓힌다고 발표했습니다(2026년 9월 브라질·인도네시아·싱가포르·태국 먼저 적용). 한국에 적용되면 설치·업데이트에 폰의 고급 설정(24시간 대기)이나 Android 개발자 인증 등록(소수 기기용 무료 제한 배포 계정 등)이 필요할 수 있습니다.

## QR 스캐너

- 'QR 찍어서 PC 추가'는 Google Play 서비스의 코드 스캐너(`play-services-code-scanner`)를 씁니다. 카메라 권한이 필요 없지만, 링크로 설치한 앱은 처음 누를 때 Play 서비스가 스캐너 부품을 내려받습니다. 앱은 `ModuleInstallClient`로 내려받기를 요청하고(3분 안에 끝나지 않으면 기다림을 멈춤), 스캐너가 `CODE_SCANNER_UNAVAILABLE`(200)로 답하면 내려받기가 실패로 보고된 뒤를 포함해 2초 간격으로 최대 1분 다시 시도합니다. 끝내 열리지 않으면 폰 기본 카메라로 찍는 방법(`codexmeter://pair` 링크)을 안내합니다.
- 정식 빌드는 R8로 축소합니다. ML Kit는 병합된 매니페스트에 적힌 `ComponentRegistrar`를 인자 없는 생성자로 만들기 때문에 `android/app/proguard-rules.pro`가 그 생성자를 지킵니다. `releaseAssets`는 매니페스트의 등록기마다 축소 기록(mapping)에 생성자가 남았는지 확인하고, 없으면 빌드를 멈춥니다(앱 1.0.0은 이 때문에 스캐너를 여는 순간 꺼졌습니다).
- 에뮬레이터 확인에는 Play 스토어 이미지가 필요합니다(`google_apis` 이미지에는 Play 스토어가 없어 스캐너 부품을 받지 못합니다). 작업 PC에는 `codexremote-play37`(Android 17), `codexremote-play36`(Android 16) 가상 기기가 있습니다.

## 로컬 검사 (작업 PC)

- 도구는 저장소 밖 `C:\Users\lizra\.android-tools\`에 있습니다(약 14GB): `jdk-17`(JAVA_HOME), `sdk`(ANDROID_HOME, 에뮬레이터·Android 17 이미지 포함), `android-home`(ANDROID_USER_HOME, 가상 기기 `codexremote-api37`은 `android-home\avd`), `gradle-home`(GRADLE_USER_HOME).
- 위 환경 변수와 `ANDROID_AVD_HOME`을 지정한 뒤 `py -3.13 android\tools\emulator_check.py`를 실행하면, 시험용 앱을 빌드하고 창 없는 에뮬레이터에서 시험 미터기 대상 전체 시나리오(등록·화면 끄기·절전·연결 안 됨·이름·순서·삭제·업데이트)를 확인합니다. 결과와 화면은 `work/phone-app/`에 남고, 에뮬레이터·시험 서버는 스스로 끕니다.
