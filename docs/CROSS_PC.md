# 다른 Windows PC에서 이어서 작업하기

다른 PC에서 [GitHub Desktop](https://desktop.github.com/)을 열고 이 저장소를 복제하세요. 프로젝트 파일을 내려받은 다음 Codex에서 해당 폴더를 열면 됩니다.

공유 저장소: [6Zman8/codex-usage-meter](https://github.com/6Zman8/codex-usage-meter)

## 처음 한 번 준비하기

1. 다른 Windows PC에 GitHub Desktop과 Codex 데스크톱 앱을 설치합니다. GitHub Desktop에는 이 저장소에 변경을 올릴 권한이 있는 GitHub 계정으로 로그인합니다.
2. GitHub Desktop에서 **File → Clone repository… → URL**을 엽니다.
3. 저장소 주소에 `https://github.com/6Zman8/codex-usage-meter.git`을 입력합니다.
4. **Local path**에서 이 PC의 작업 폴더를 정하고 **Clone**을 누릅니다. 예: 사용자 폴더 아래 `Projects\codex-usage-meter`. PC마다 경로가 달라도 됩니다. OneDrive 같은 동기화 폴더 밖에 두고, 코드 전달은 GitHub로 처리하는 편이 관리하기 쉽습니다.
5. Codex에서 로컬 프로젝트를 추가하거나 열 때 방금 복제한 폴더를 선택합니다. `README.md`, `AGENTS.md`, `build.ps1`, `src`가 함께 있는 최상위 폴더입니다. Codex 로그인은 해당 PC에서 별도로 진행합니다.
6. 프로그램을 확인하려면 폴더 안의 **build.cmd**를 더블 클릭합니다. 완료 후 **bin → CodexUsageMeter.exe**를 더블 클릭합니다. 이미 이 폴더의 미터기를 실행 중이면 저장 후 직접 종료하고 다시 빌드합니다.

현재 빌드는 64비트 Windows의 .NET Framework 4.x C# 컴파일러와 WPF를 사용합니다. Visual Studio나 별도의 .NET SDK, npm 설치는 필요하지 않습니다. `build.ps1`은 해당 PC의 Windows 설치 폴더 아래 `Microsoft.NET\Framework64\v4.0.30319`를 사용하므로 해당 구성요소가 있어야 합니다. 컴파일러를 찾을 수 없거나 빌드가 실패하면 열린 창의 오류 내용을 Codex에 알려 주세요. 창을 닫기 전 오류를 확인할 수 있도록 빌드 창은 대기합니다.

이 절차의 메뉴는 [GitHub 복제 안내](https://docs.github.com/en/desktop/adding-and-cloning-repositories/cloning-and-forking-repositories-from-github-desktop?platform=windows)를 기준으로 합니다. 로컬 폴더 연결과 프로젝트 지침은 [OpenAI 공식 프로젝트 안내](https://learn.chatgpt.com/docs/projects)를 참고하세요.

## 컴퓨터를 옮길 때마다

| 시점 | 누를 항목 | 의미 |
| --- | --- | --- |
| 작업 시작 전 | GitHub Desktop에서 저장소와 작업할 브랜치 선택 → **Fetch origin** → 표시되면 **Pull origin** | 이전 PC에서 올린 변경을 받습니다. |
| 작업 종료 후 | 변경 파일 확인 → 왼쪽 아래 **Summary** 입력 → **Commit to main** 또는 현재 브랜치로 커밋 → **Push origin** | 변경을 이 PC에 기록하고 GitHub에 올립니다. |
| 새 브랜치로 작업한 경우 | **Publish branch**가 보이면 게시하고, 다른 PC에서도 같은 브랜치 선택 | 아직 main에 합쳐지지 않은 작업도 이어갑니다. |

기본 브랜치는 `main`입니다. **Commit만 하면 다른 PC에는 전달되지 않으므로 Push까지 완료**해야 합니다. [GitHub 동기화 안내](https://docs.github.com/en/desktop/working-with-your-remote-repository-on-github-or-github-enterprise/syncing-your-branch-in-github-desktop)

두 PC에서 동시에 같은 파일을 수정하지 않는 흐름을 권장합니다. 충돌이나 저장하지 않은 변경 경고가 나오면 파일을 버리거나 강제 덮어쓰지 말고 Codex에 해결을 요청하세요. 현재 PC의 기존 폴더는 그대로 쓰면 됩니다. GitHub Desktop에 아직 보이지 않는다면 **File → Add local repository…**로 기존 프로젝트 폴더를 등록하세요.

## 함께 전달되는 것과 PC에 남는 것

- GitHub에 올라간 소스, 아이콘, 빌드 파일, README, 프로젝트 지침과 변경 이력은 다른 PC에서도 받습니다.
- `bin`, `obj`, `artifacts` 등 Git에서 제외한 폴더와 아직 커밋하지 않은 파일은 전달되지 않습니다. 필요한 실행파일은 해당 PC에서 빌드합니다.
- 미터기 계정 연결은 각 PC에서 다시 진행합니다. `%LOCALAPPDATA%\CodexUsageMeter`의 계정 자료와 레지스트리 설정은 복사하지 않습니다.
- `.codex`의 인증정보·대화·메모리·SQLite·실행 상태는 이 저장소로 옮기지 않습니다. 이 안내는 프로젝트 소스를 공유하는 절차입니다.
- 새 PC의 Codex에는 `AGENTS.md`, `README.md`, `docs/CROSS_PC.md`를 읽고 현재 Git 상태부터 확인하도록 요청하면 됩니다. 작업 결정이나 남은 과제는 프로젝트 문서에 기록한 뒤 커밋하면 이어받을 수 있습니다.

## 프로젝트 구조와 확인 방법

- `src`: C# 소스, WPF 화면, 기존 회귀검사
- `assets`: 앱 아이콘 원본
- `build.cmd`: 더블클릭 빌드 진입점
- `build.ps1`: 실제 빌드, `-OutputName`으로 별도 검사 실행파일 이름 지정 가능
- `docs/RELEASING.md`: 정식 버전 배포 절차
- `AGENTS.md`: 다른 PC에서도 함께 읽을 프로젝트 작업 지침

빌드와 검사는 실행 중인 기존 미터기를 덮어쓰지 않는 별도 파일로 수행합니다. 사용자 화면을 띄우지 않는 `--update-ui-self-test`, `--rate-limit-self-test`, `--account-switch-self-test` 검사가 포함되어 있습니다. 계정 전환 검사는 가짜 프로필을 사용하며 실제 계정 전환 성공을 의미하지 않습니다. 실제 다른 PC에서의 로그인·기기 자원·실행 확인은 그 PC에서 따로 확인해야 합니다.
