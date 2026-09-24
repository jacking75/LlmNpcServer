# 최소 월드

`npc init <새 폴더>`가 이 템플릿을 복사한다. 존 2개, POI 12개, 아키타입 3개, 아이템 8개, NPC 60명이다.

`world_flags.json`은 엔진 빌드와 일치해야 한다. 플래그를 추가하거나 이름과 bit를 바꾸면 저장소의 `masterdata/world_flags.json`부터 수정하고 다시 빌드한다. `npc explain core`가 고정된 어휘를 설명한다.

POI, 존, 아키타입을 바꾼 뒤 저장소 루트에서 파생물을 다시 만든다.

```powershell
dotnet run tools/gen_poi_distances.cs -- --masterdata <새 폴더>
dotnet run tools/gen_npcs.cs -- --masterdata <새 폴더> --population 60
dotnet run --project tools/Npc.Cli -- validate --masterdata <새 폴더>
```

서버 실행 시 `--planstore <새 폴더>/planstore`를 함께 지정해 예제 마을의 플랜과 섞이지 않게 한다. 프롬프트 파일은 시작 예제로 복사되므로 우리 세계의 직업과 장소에 맞춰 검토한다.
