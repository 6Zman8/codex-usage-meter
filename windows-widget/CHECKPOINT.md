# Windows 위젯 제공자 작업 기록

- 범위: 별도 .NET 8 C# 제공자. 본체의 .NET Framework 빌드, 인증/계정 자료, Windows 개발자 모드·인증서·등록 상태를 변경하지 않습니다.
- 계약: 부모 작업이 지정한 schemaVersion 1 snapshot.json, app-path.txt, open/refresh 동작과 3분 관측 유효기간을 따릅니다.
- 단계: 카드/상태 회귀검사 RED → 구현 GREEN → 공식 COM 제공자 및 패키지 구성 → 격리 CLI·카탈로그 읽기 검사 → 배포 폴더/ZIP 검증.
- 출력: bin/windows-widget/CodexUsageMeter.WindowsWidget.zip. 새 긴 로그는 work/v1.6.0/widget-provider/에 저장합니다.
- 실제 보드 등록은 개발자 모드 승인 후 부모 작업이 수행합니다. 등록/화면 실표시 미확인은 별도 기록합니다.
# 2026-10-09 최종 제공자 후보

- `bin/windows-widget/package/`와 `bin/windows-widget/CodexUsageMeter.WindowsWidget.zip` 봉인. ZIP 41,882,235 bytes, SHA256 `6299B10B988B17A99EEB547984441EB65466C1DBDAD79D0A8E1101CD18A1B2EC`.
- 27개 회귀검사, self-contained WinRT 활성화·IWidgetProvider COM 전달, MakeAppx 구조 검증, unpackaged breakaway/exit1 fixture 통과. 로그는 `work/v1.6.0/widget-provider/`.
- 공식 Microsoft Widgets host config/CSS + AdaptiveCards 3.0.5로 숨김 렌더 검증: S/M/L 139.9/251.7/315.5px, 허용146/304/462px 이내. JSON validation 및 텍스트 잘림 0. `previews/cards.png`, `previews/layout-results.json`.
- 실제 lowercase plan/prolite 표기, 초기화 경계 갱신 대기, 계정별 관측 시각, 작은 카드 2×2, worker에서 실행결과 최대8초 관측을 포함합니다. 기존 본체·계정 자료는 실행 검사에 사용하지 않았습니다.
- 재개점: 부모 에이전트가 승인받은 패키지 등록 후 `windows-widget/tests/Invoke-PackagedLaunchProbe.ps1`을 실행합니다. provider identity 존재 및 child APPMODEL_ERROR_NO_PACKAGE(15700), exit1 실패알림을 확인해야 합니다. 실보드 고정·렌더·실계정 갱신은 아직 이 하위 작업에서 확인하지 않았습니다.
