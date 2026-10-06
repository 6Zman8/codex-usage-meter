# Chrome / Tampermonkey 구독 연결

## 목적과 사용 경로

내장 WebView 로그인 대신 평소 Chrome의 세션과 저장된 비밀번호를 이용합니다. 초기화권 아래 구독 날짜 → 스크립트 설치 → Tampermonkey 설치 → 미터기 날짜 다시 클릭 → 크롬 열기 순서입니다. 최초 연결 후 같은 Chrome 프로필의 ChatGPT 구독 페이지를 열면 반영합니다. 크롬을 닫은 동안 지속 조회하는 기능은 아닙니다.

프로그램은 GitHub Release → 내장 업데이트로 전달합니다. 스크립트 설치 URL은 이 저장소 `browser/codex-meter-subscription.user.js`의 raw URL이며 `@updateURL`/`@downloadURL`도 동일합니다. 별도 다운로드 폴더 전달본은 만들지 않습니다.

## 데이터와 연결

- Tampermonkey는 정상 ChatGPT 계정 조회 응답을 관찰합니다. HTTPS의 정확한 accounts/check 경로만 인정하며, 크롬 요청 헤더나 인증 토큰을 읽지 않습니다.
- 미터기에 전달하는 필드는 계정 ID, 현재 플랜, 갱신/종료/전환 날짜, 자동 갱신 여부, 관측 시각입니다. 비밀번호, 쿠키, 대화, 결제 주소, 결제수단은 제외합니다.
- 미터기는 `127.0.0.1:43129`에서 제한된 POST 하나만 받습니다. 임의 사이트의 CORS 요청은 허용하지 않으며 정확한 Host와 256-bit 연결 키를 확인합니다. 헤더/본문 크기, 동시 연결 수, 읽기 시간을 제한합니다.
- 연결 키는 Windows 사용자 DPAPI로 암호화한 `%LOCALAPPDATA%/CodexUsageMeter/chrome-bridge/connection.bin`과 해당 userscript의 전용 저장소에 유지합니다. 미터기 버튼이 여는 URL의 fragment로 첫 연결을 하고 스크립트가 즉시 fragment를 제거합니다. 키를 로그에 남기지 않습니다.
- 수신 시 현재 미터기 계정 ID/이메일/플랜을 다시 검증합니다. 10분 넘은 응답·미래 시각·더 오래된 응답은 거부합니다. 날짜 저장과 최신 시각 검사를 같은 잠금에서 처리합니다.
- Chrome 연결 이후 지연된 내장 WebView 응답은 날짜나 연결 방식을 덮어쓸 수 없습니다. 기존 웹 프로필 파일은 삭제하지 않습니다.
- Chrome 날짜가 24시간을 넘거나 기한이 지나면 저장된 값이라는 설명과 `재확인 필요`를 표시하며 다음 달로 임의 계산하지 않습니다. 최신 응답에 날짜가 없으면 이전 날짜를 유지하지 않습니다.

## 검사와 실제 사용 경계

- `--chrome-subscription-self-test 결과파일`: 가짜 계정으로 identity/plan, 관측 순서, legacy 덮어쓰기, 날짜 유효성, DPAPI 재사용, 실제 TCP POST와 잘못된 key/host/CORS/oversize 거부, auth 보존을 검사합니다. `--layout-self-test`와 전체 검사에도 포함됩니다.
- `node browser/subscription.test.cjs`: 별도 VM에서 fetch/XHR 관찰, 일반 페이지에서 구독 페이지로 이동, 최초 연결과 fragment 제거, 최소 필드만 전송, 중복 억제를 검사합니다.
- `--update-ui-self-test 결과파일 미리보기폴더`: 숨김 WPF에서 Chrome 연결 안내의 버튼과 작은 창을 검사하고 PNG를 저장합니다.
- 실제 Chrome 확장 설정 페이지는 브라우저 자동화 보안 정책이 접근을 차단했습니다. 우회하거나 확장 저장소를 직접 수정하지 않았습니다. 실제 Tampermonkey 설치·실계정 응답 후킹·미터기까지의 최종 연결은 사용자 최초 설치 후 확인해야 합니다. 위의 격리 검사 성공과 구분합니다.
- 다른 PC는 Chrome/Tampermonkey 연결을 따로 진행합니다. 계정·키·웹 세션을 Git이나 PC 사이에 복사하지 않습니다.

## 작업 체크포인트 (2026-10-06)

- 구현 및 가짜 계정 TCP/스크립트 테스트 완료. 최초 24시간 경과 날짜 시험 실패 후 stale 표시 구현으로 통과.
- 별도 리뷰에서 소유권 및 동시 요청 순서 문제를 찾아 저장소 잠금으로 수정, 재검토에서 추가 확정 중요 문제 없음.
- 최종 버전/배포/해시 및 설치 경계는 `releases/v1.3.0.md`에 기록합니다.
