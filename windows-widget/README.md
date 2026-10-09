# Windows 11 사용량 위젯 제공자

사용 경로는 미터기의 Windows 위젯 연결 메뉴에서 설치한 다음 `Win+W → 위젯 추가 → Codex 사용량`을 고정하는 것입니다. 이 폴더는 별도 .NET 8 제공자의 소스이며 기존 WPF 본체 빌드는 변경하지 않습니다.

## 빌드와 배포 계약

프로젝트 루트의 `build-windows-widget.ps1`을 실행하면 `bin/windows-widget/package/`와 `bin/windows-widget/CodexUsageMeter.WindowsWidget.zip`을 생성합니다. 루트에 AppxManifest.xml과 CodexUsageMeter.WidgetProvider.exe가 있습니다. 스크립트는 설치된 .NET 8 SDK 또는 프로젝트 obj/widget-tools의 SHA-512 검증된 휴대용 SDK를 사용합니다. Windows나 사용자 환경에 SDK를 설치하지 않습니다. 긴 빌드 로그와 검사는 work/v1.6.0/widget-provider/에 보존합니다.

배포 단위는 전체 ZIP입니다. .NET 및 Widgets 런타임, SDK가 병합한 EXE manifest와 패키지의 WinRT 활성화 fragment를 포함합니다. EXE만 복사하면 작동하지 않습니다. 패키지 식별자는 CodexUsageMeter.WindowsWidget, 게시자는 CN=CodexUsageMeter, 버전은 1.6.0.0, 아키텍처는 x64입니다. COM CLSID는 6BA7A0D2-8E9C-4F5D-A263-624316ECC101, 위젯 정의 ID는 CodexUsageSummary입니다.

본체 설치 코드는 이 ZIP을 버전별 사용자 폴더에 푼 뒤 사용자 컨텍스트에서 `Add-AppxPackage -Register <절대 경로>/AppxManifest.xml`로 등록합니다. 개발자 모드가 필요합니다. 빌드 스크립트와 제공자 CLI는 개발자 모드·인증서 신뢰·패키지 등록을 변경하지 않습니다. Microsoft Store 외 배포의 위젯 조건은 공식 샘플을 참고합니다.

## 데이터와 동작

- 읽는 데이터: `%LOCALAPPDATA%/CodexUsageMeter/windows-widget/snapshot.json` 하나, UTF-8 schemaVersion 1. writtenAtUtc, running, accounts[최대 4개]의 number/plan/status/observedAtUtc/primary/secondary를 사용합니다. label은 개인 식별정보·마크다운 노출을 피하기 위해 안정된 `계정 N`으로 표시합니다.
- 한도에는 remainingPercent(0~100), resetsAtUtc, durationMinutes가 들어갑니다. null·누락·잘못된 범위는 미제공이며 실제 측정 0%와 구분합니다.
- 3분 초과 관측, 시각 미상, 미래로 1분 이상 치우친 관측은 오래됨으로 표시합니다. running=false면 미터기 꺼짐을 표시합니다.
- 초기화 시각을 지난 한도는 새 관측 전까지 갱신 대기로 표시합니다. 큰 카드의 시각은 파일 작성 시각이 아닌 계정별 실제 관측 시각입니다. 작은 카드는 2×2 요약이며 5h는 5시간, 주는 주간, —는 미제공입니다.
- 같은 폴더의 app-path.txt에 본체의 절대 EXE 경로 한 줄을 저장합니다. 열기는 인수 없이 실행하고 새로고침은 `--widget-refresh`로 실행합니다. 명령 셸이나 임의 위젯 action data를 실행하지 않습니다.
- 본체 실행에는 Windows의 명시적 Desktop App breakaway 정책을 적용하여 기존 비패키지 프로필을 유지합니다. COM 콜백 밖의 작업자가 최대 8초 동안 자식 종료 코드를 확인하고 실패를 카드에 알립니다. 정상적으로 계속 실행 중인 본체는 성공으로 취급합니다.
- 본체의 60초 갱신 결과를 보드 활성 시 최대 15초 간격으로 반영합니다. 비활성 위젯에는 주기 갱신을 보내지 않습니다. 인증·세션·accounts 자료를 열지 않습니다.
- 새 본체는 `widgetPages`에 **미터기의 위젯모드와 같은 XAML·배치 엔진으로 그린 화면**을 제공합니다. 원형 게이지·갱신 시간·초기화권·구독 날짜·PC 상태와 저장된 카드 순서·요소 위치·크기·숨김을 반영합니다. 이메일·계정 별칭·원문 오류는 이미지와 대체 텍스트에 넣지 않습니다. 본체는 15초 간격으로 화면을 갱신하며 계정 조회 주기는 기존 60초입니다.
- Windows가 WPF 화면을 직접 호스팅하지 않으므로 본문은 이미지입니다. 화면 클릭/열기에서 본체를 열고, 새로고침과 다음 계정은 위젯 버튼으로 조작합니다. 저장된 카드 순서를 유지하며 작은 크기는 1장, 중간은 최대 2장, 큰 크기는 최대 3장씩 넘겨 봅니다. 이력·계정 전환 버튼은 이미지에 그리지 않습니다. Windows 제목줄·고정·메뉴는 Windows가 관리합니다. 구버전 본체의 자료만 있을 때는 기존 텍스트 요약을 유지합니다.

## 백그라운드 검사

- `CodexUsageMeter.WidgetProvider.exe --self-test <결과 JSON>`: 계정 자료를 읽지 않는 카드/상태 회귀검사.
- `--card-preview <출력 폴더> [가상 snapshot JSON 경로]`: 가상 계정 4개 또는 지정한 검사 자료의 small.json, medium.json, large.json과 snapshot.example.json. 본체의 `--windows-widget-self-test`가 생성한 `rendered-snapshot.json`으로 공통 화면을 검사할 수 있습니다. 실제 보드 스크린샷이 아닙니다.
- `--catalog-probe <결과 JSON>`: Windows 앱 확장 카탈로그에서 이 패키지의 등록 여부만 읽습니다. 등록·설치·보드 고정을 하지 않습니다.
- `--runtime-probe <결과 JSON>`: 배포 폴더의 native WinRT 활성화와 IWidgetProvider COM 인터페이스 전달을 가상 제공자로 확인합니다. WidgetManager나 실제 위젯을 변경하지 않습니다.
- `--launch-context-test <Launch Context Probe.exe 경로> <결과 JSON>`: 빌드가 work 폴더에 생성한 무인 fixture를 실행하여 본체 실행 정책·인수·실패 알림을 검사합니다. 실제 본체·계정 자료를 사용하지 않습니다. 등록 후 패키지 AUMID로 이 옵션을 활성화하면 provider identity 유지와 child의 APPMODEL_ERROR_NO_PACKAGE(15700)를 함께 확인할 수 있습니다.
- 옵션 없이 실행하거나 COM 인수를 받으면 숨김 COM 서버로 대기합니다. 보드가 위젯을 모두 삭제하면 종료합니다.

카드 검사와 카탈로그 읽기 성공은 Win+W 실표시 성공을 뜻하지 않습니다. 최종 확인은 사용자 승인 후 등록, 보드 추가, 크기 변경, 상태·잔여량 갱신, 열기/새로고침, 본체 재시작 복원을 따로 확인해야 합니다.

## 공식 근거

- https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/Widgets/cs-console-packaged
- https://learn.microsoft.com/en-us/windows/apps/develop/widgets/implement-widget-provider-cs
- https://learn.microsoft.com/en-us/windows/apps/develop/widgets/widget-provider-manifest
- https://github.com/microsoft/WindowsAppSDK-Samples/blob/main/Samples/Widgets/README.md
- https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Widgets/2.0.5
- https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps
- https://github.com/microsoft/WindowsAppSDK/blob/main/dev/Deployment/DeploymentManager.cpp
