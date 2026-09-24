# 기여 안내

구현·테스트·문서의 단일 규칙은 [CLAUDE.md](CLAUDE.md)에 있다. 작업별 파일 지도는 [CODEMAP.md](CODEMAP.md)다. 여기에는 규칙을 복사해 두지 않는다. 콘텐츠 변경에는 [마스터데이터 레퍼런스](docs/reference_masterdata.html)와 [Studio 설명서](docs/npc_studio_manual.html)를 함께 본다.

처음 환경을 확인하려면 `dotnet build -c Release`, `dotnet run --project src/Npc.Host -- doctor`, `dotnet run --project tools/Npc.Cli -- validate`를 실행한다. 변경 범위에 맞는 테스트와 레퍼런스 문서를 같은 변경에 넣는다.
