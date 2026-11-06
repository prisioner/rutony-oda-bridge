# Rutony ODA Bridge

Версия — **1.0.0**. [Заметки выпуска](docs/releases/1.0.0.md).

Установка без сборки: [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3834556283)
или готовый ZIP из [GitHub Releases](https://github.com/prisioner/rutony-oda-bridge/releases).
Распакуйте ZIP в отдельную папку и следуйте [инструкции установки](docs/oda-setup.md).
Готовый пакет не требует .NET SDK или Node.js.

Автономный мост ODA для RutonyChat: выбранные платежи поступают в штатный
поток Donate/OnDonate с именем, суммой, валютой, текстом и message_id.
Обычные оповещения и пользовательские скрипты RutonyChat получают эти события.

Мост содержит `RutonyOda.dll`, автономный `rutony_oda.cs` и локальную панель
`http://127.0.0.1:18766/control`. По умолчанию передаются только донаты ODA;
импортированные сервисы выбираются отдельно. OAuth использует приложение,
созданное самим пользователем; секреты защищены Windows DPAPI.

Пакет открывает панель через **Открыть ODA.url** или **Панель ODA.html**
с выбором порта. При включении автономный скрипт показывает в RutonyChat
ссылку **Открыть панель ODA** с выбранным портом. Если пользовательская
тема скрывает ссылки, панель можно открыть ярлыком из пакета.

Для разработчиков: сборка из самостоятельного клона на Windows
требует .NET SDK 9, Node.js и PowerShell:

```powershell
./scripts/build.ps1 -Check
```

Результат — `dist/RutonyOda`. Сборка и проверки не требуют установленного
RutonyChat или аккаунта ODA. Они используют отдельные временные данные
и имитаторы HTTP/WebSocket/native-приёмника и не делают платежей.
Особенности своего OAuth-приложения и установки проверяйте по
[инструкции подключения](docs/oda-setup.md);
[границы совместимости](docs/runtime-compatibility.md) описаны отдельно.

[Разработка](docs/project/development.md) ·
[Архитектура и данные](docs/project/architecture.md) ·
[Качество](docs/project/quality.md) ·
[Доставка](docs/project/delivery.md) ·
[Публичные API-источники](docs/research/oda-contracts.md) ·
[Инструкции агенту](AGENTS.md)

Лицензия — [MIT](LICENSE). Copyright (c) 2026 Aleksandr Klimenkov.
Расширение предоставляется как есть, без гарантий. При распространении
сохраняйте уведомление об авторских правах и текст лицензии.

[Security](SECURITY.md) · [Участие в разработке](CONTRIBUTING.md) ·
[Изменения](CHANGELOG.md) · [Комплект Steam Workshop](docs/workshop/README.md).
