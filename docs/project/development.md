# Среда и разработка

Библиотека и изолированный стенд используют .NET 9/C# и framework reference
`Microsoft.AspNetCore.App`. Локальный UI — HTML/CSS/JavaScript без CDN
и npm-зависимостей. SDK задаёт `global.json`; `NuGet.Config` отключает
внешние package sources. Установленный RutonyChat не нужен для сборки.

Принятая среда — Windows, PowerShell, .NET SDK 9 и Node.js. Windows нужна
проверкам настоящего DPAPI; Node проверяет синтаксис браузерного JS и tests/*.test.mjs, когда они есть.
Версия SDK — из `global.json`, допустимый roll-forward — `latestFeature`.
Для работы готового пакета SDK и Node.js не требуются.

## Команды из корня клона

```powershell
./scripts/build.ps1 -Check
```

Выполняет проверки ядра и loopback HTTP/WebSocket, изолированный native
контракт с реальным тонким скриптом, JS syntax и обнаруженные tests/*.test.mjs, сборку PreviewHost и Release
библиотеки. Пакет — `dist/RutonyOda`; его состав описан в
[доставке](delivery.md). Без `-Check` выполняется только сборка пакета.
CLI home создаётся внутри игнорируемой `.runtime` этого клона.
Повторная сборка создаёт пакет с нуля; прежний сохраняется в игнорируемой
`.runtime/package-backups`. Чужие файлы из прежнего dist в новый пакет не попадают.

```powershell
dotnet build tools/PreviewHost -c Release
dotnet run --project tools/PreviewHost -- src/RutonyOda .runtime/preview 18767
```

Откройте напечатанный loopback-адрес и включите передачу: стенд доставит
один синтетический EUR 12.34 собственному приёмнику. Он не вызывает реальные
глобальные события RutonyChat. Папка данных должна быть пустой либо ранее
помеченной самим preview. Не передавайте ему рабочий каталог моста.
Остановка — Ctrl+C; процессы production и их данные не затрагиваются.

## Структура

| Каталог | Содержимое |
|---|---|
| `src/RutonyOda` | Библиотека, HTTP/OAuth/STOMP, журнал и `web` |
| `integration` | Тонкий `rutony_oda.cs` и штатная JSON-схема параметров |
| `tests/RutonyOda.Checks` | Ядро, DPAPI, loopback API/WS, модель broker lifecycle |
| `tests/RutonyNativeContract.Checks` | Actual script + fake native + независимый OnDonate subscriber |
| `tools/PreviewHost` | Изолированный визуальный стенд с FakeOda из этого репозитория |
| `scripts` | Сборка и установщик собственного пакета |
| `launcher` | Свой `.url` и HTML с одним выбором порта |

Все ProjectReference и Compile links разрешаются внутри этого репозитория.
Нет обязательных файлов в родительском или соседнем проекте. Бинарники
RutonyChat не распространяются. Сборка против фактической установленной
DLL и загрузка скрипта в настоящий процесс — отдельные ручные проверки.

[Качество](quality.md) · [Архитектура](architecture.md) · [Совместимость runtime](../runtime-compatibility.md)

## Целостность пакета и переносимость артефактов

Canonical сборка требует Git и передаёт полный `git rev-parse HEAD` как
SourceRevisionId. `Directory.Build.props` сохраняет net9.0 и SDK из global.json,
задаёт deterministic Release, portable PDB и отображение путей текущего
клона в `/_/`. InformationalVersion сохраняет SHA. Изменённое рабочее дерево
может содержать незакоммиченные изменения: SHA описывает базовый commit,
поэтому выпуск собирайте из чистого checkout.

`-Check` также запускает `tests/delivery.tests.ps1` на временных fake
Rutony-папках в игнорируемой `.runtime`: missing/hash/traversal/duplicate
manifest, locked destination, source/target junctions, stage/backup/commit
failures, rollback failure/recovery, прерывание дочернего процесса и первая
установка. Тесты не обращаются к настоящему RutonyChat и не требуют аккаунтов.
Искусственные сбои и mock Get-Process существуют только внутри fixture.

`tools/ArtifactChecks` проверяет PE deterministic marker, portable CodeView,
соответствие PDB/PE, document paths `/_/` и SHA в InformationalVersion.
PDB остаётся build-артефактом и не входит в пользовательский пакет.
Пакет собирается отдельным `scripts/package.ps1 -SourceRevision <SHA>`
по allowlist из `scripts/package-contract.ps1`; обычная build-команда
делает это сама. Прежний dist сохраняется в `.runtime/package-backups`.

Все tracked C#-файлы имеют committed policy `*.cs text eol=lf` в
`.gitattributes`: компилятору нужны одинаковые байты исходников независимо
от `core.autocrlf`. Добавление attributes не переписывает существующий checkout.
Если в нём остались CRLF или mixed EOL, используйте свежий checkout либо явно
сохраните затронутые C#-файлы с LF, предварительно сохранив свою работу.
Gate проверяет фактические байты и отказывает до сборки checkout roots;
он не меняет исходники автоматически. BOM и текст исходников сохраняются.

После commit из чистого checkout соберите **итоговый canonical пакет** и
проверьте его тем же SDK из `global.json`:

```powershell
./scripts/build.ps1
./scripts/check-reproducibility.ps1
```

Gate требует чистый Git HEAD, LF-исходники, существующие canonical DLL и
соответствующий portable PDB, полный валидный dist manifest с текущим SHA
и DLL пакета, совпадающую с canonical DLL. Он проверяет PE/PDB identity и
revision, создаёт два настоящих detached Git worktree одного HEAD в
`.runtime/reproducibility` и собирает их тем же SDK. SHA-256 **обоих DLL и
PDB** должны совпасть между checkout roots и с canonical результатом;
DLL dist тоже должна совпасть. Старый или иной пакет/артефакт вызывает ошибку
с диагностикой; gate не пересобирает canonical пакет молча. Исправьте причину
и явно повторите `scripts/build.ps1` и gate. PDB нужен проверке, но не входит
в пользовательский пакет. Worktree и логи сохраняются для исследования.
Гарантия относится к одному чистому SHA, SDK и ОС, а не к разным SDK.

После такой сборки отдельный regression проверяет, что gate отвергает
изменённые canonical DLL и DLL пакета даже с корректным обновлённым manifest:

```powershell
pwsh -NoProfile -File tests/reproducibility.tests.ps1
```

Он временно меняет только generated артефакты, восстанавливает их точные
исходные байты в `finally` и сохраняет evidence в `.runtime`. Delivery fixtures
поддерживают PowerShell 7 и Windows PowerShell 5.1; crash-child в существующем
runner запускает `pwsh`, поэтому требует PowerShell 7 и не доказывает прерывание
процесса Windows PowerShell 5.1.


## Версия и CI

`Directory.Build.props` задаёт единственную рабочую `Version`;
InformationalVersion содержит её и полный SourceRevisionId. Текущее
`1.0.0` — версия MVP. Подробности ZIP и release manifest —
в [доставке](delivery.md), возможности и ограничения —
в [заметках 1.0.0](../releases/1.0.0.md).
Версия не меняет runtime HTTP-контракты и native facade.

`build.ps1 -Check` также выполняет `tests/release.tests.ps1`: старые/новые
schema 1 manifest, наследование версии из props, отказы неверной version/SHA,
повреждённого/лишнего payload и ZIP, побайтовое соответствие ZIP проверенному
пакету и независимость архива от времени исходных файлов. Fixtures используют
только синтетические данные под `.runtime`. Их можно запустить отдельно:

```powershell
pwsh -NoProfile -File tests/release.tests.ps1
```

[GitHub Actions workflow](../../.github/workflows/ci.yml) запускается на push,
pull_request и workflow_dispatch, использует `windows-2025`, SDK из
`global.json` (9.0.200) и Node.js 24.16.0. SDK устанавливается в отдельную
папку runner. Отдельный PowerShell-шаг проверяет literal `sdk.version`
и передаёт его через step output в `dotnet-version` action; локальная
политика rollForward не расширяет запрос CI. Выбранные версии явно проверяются:
preinstalled SDK с более высокой feature band не должен незаметно заменить требуемую версию.
Локально сохраняется существующий `latestFeature` из global.json.

Официальные actions закреплены полным commit SHA; обновляя pin, сверьте его
с официальным release и пересмотрите изменения. В candidate job token имеет только
`contents: read`, checkout не сохраняет credentials и получает всю историю.
Отдельный job после успешных проверок push-тега версии получает `contents: write`
и `actions: read` для скачивания artifacts того же run и создания draft Release
через `GITHUB_TOKEN`. Публикация вручную; установки в настоящее приложение нет.
Новые запуски той же ветки/PR отменяют прежние; timeout job — 30 минут.
Workflow выполняет publication scanner и его fixtures, полный build-Check,
реальную reproducibility gate с двумя checkout, её негативные fixtures,
создание/проверку кандидата и offline Workshop staging с fixtures.
Артефакты хранятся 14 дней: только `dist/*.zip`, release manifest и
`dist/Workshop/**`, `dist/WorkshopMaterials/**`; bin/obj/runtime/PDB не загружаются.

Результат CI относится к указанному в запуске commit. Для соответствия SHA
в артефактах исходникам собирайте пакет из чистого checkout; при грязном
дереве SHA описывает только базовый commit.

## Проверка содержимого Git

```powershell
pwsh -NoProfile -File scripts/check-publication.ps1
pwsh -NoProfile -File tests/publication.tests.ps1
```

Scanner читает tracked-файлы и историю, достижимую из всех локальных refs.
Он проверяет известные сигнатуры секретов, имена собственных runtime-файлов
и переносимость ссылок в текущем Markdown. Игнорируемые и untracked-файлы,
локальный Git config и переменные окружения не читаются. Внешние URL
не запрашиваются. Неизвестные форматы секретов могут остаться незамеченными;
проверка не заменяет просмотр состава изменения. Shallow clone не позволяет
проверить полную историю и завершает команду с ошибкой.

Офлайн-проверки подготовки GitHub Release: `pwsh -NoProfile -File tests/github-release.tests.ps1`.
Они проверяют идентичность пакета, вложенные хеши, состав ZIP и сохранность существующего output.
