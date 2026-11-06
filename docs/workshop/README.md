# ODA Bridge: комплект Steam Workshop

Репозиторий самостоятельно собирает содержимое item и отдельные материалы
карточки. `metadata.json` — переносимые данные карточки, не файл загрузчика
`workshopitem.meta`. Значение `status: draft` обозначает формат входа
упаковщика. Оно не определяет видимость item в Steam. Версия и Git revision
берутся из исходного package manifest.

## Сборка из корня клона

На Windows с .NET SDK из `global.json`, Node.js и PowerShell выполните:

```powershell
./scripts/build.ps1 -Check
./tests/workshop.tests.ps1
./scripts/prepare-workshop.ps1
```

Последняя команда читает `dist/RutonyOda/package-manifest.json` и проверяет
точный список файлов, длины и SHA-256 ручного пакета. Можно явно задать
`-PackageDirectory` другого проверяемого пакета. Старый manifest schema 1
без версии поддерживается; указанная version переносится в Workshop manifest.
Скрипт работает локально и не запускает RutonyChat, Steam или загрузчик.

- `dist/Workshop/RutonyOda/` — содержимое item.
- `dist/WorkshopMaterials/RutonyOda/` — `README.md`, `description.txt`,
  `metadata.json`, `preview.png`, `preview.svg`, `workshop-manifest.json`.

Предыдущие результаты сохраняются в `.runtime/workshop-backups/`,
рабочие стадии — в `.runtime/workshop-stage/`. Исходный пакет не изменяется.
Manifest среди материалов содержит Git revision исходного пакета и
относительные пути, размеры и хэши payload.

## Состав содержимого

```text
RutonyOda/
  rutony_oda.cs
  rutony_oda.json
  LICENSE
  RutonyOda/
    RutonyOda.dll
    LICENSE
    web/
```

В корне содержимого находятся три отдельных файла и папка `RutonyOda`.
В payload нет установщика, PDB, RutonyChat.dll, тестов, пользовательских данных,
ZIP, материалов карточки или `workshopitem.meta`. JSON параметров определяется
именем CS-файла; название карточки не переименовывает CS, DLL или папку.

SVG — исходник геометрической обложки без внешних изображений и шрифтов;
PNG — 512×512, менее 1 000 000 байт. Описание содержит ссылку на публичные
исходники и короткое раскрытие использования ИИ при разработке.
Материалы распространяются под той же MIT-лицензией, что и расширение.

Установка, выбор `PackageDirectory`, расположение данных и обновление описаны
в [инструкции подключения](https://github.com/prisioner/rutony-oda-bridge/blob/master/docs/oda-setup.md). Синтетические fixtures проверяют
локальную упаковку; поведение подписки, обновления и отписки зависит от Steam
и конкретной установки RutonyChat.

Технические источники:
[Rutony: GetScriptDirectory](https://github.com/rutony/rutonychat-testscripts/wiki/Общие-принципы-написания-скриптов-для-программы-RutonyChat),
[Steam Workshop](https://partner.steamgames.com/doc/features/workshop/implementation).
