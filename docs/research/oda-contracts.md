# Публичные контракты ODA

Ниже зафиксированы upstream-версии, на основании которых реализованы
OAuth, подписка, STOMP и нормализация событий. Ссылки ведут на публичные
исходники; код ODA не копируется и не является зависимостью сборки.
Текущая реализация и гарантии — в [архитектуре](../project/architecture.md).

| Контракт | Публичный источник |
|---|---|
| Свои приложения и Redirect URIs | [ApiPage.tsx, c8184d0](https://github.com/OpenDonationAssistant/oda-widget-page/blob/c8184d0e409e9a90bf4a94b3a64a139c9db0a81a/src/pages/Api/ApiPage.tsx) |
| Подписка внешнего клиента | [AddSubscription.java, 53f0b95](https://github.com/OpenDonationAssistant/oda-subscriptions-service/blob/53f0b95ceb65d587db02764d18bf4ec42a05b877/src/main/java/io/github/opendonationassistant/subscription/commands/AddSubscription.java) |
| HistoryItemEvent | [HistoryItemEvent.java, 0ac1150](https://github.com/OpenDonationAssistant/oda-libraries/blob/0ac1150e2e71bc7b6a5bade146a3b81a782c235d/rabbit-conf/src/main/java/io/github/opendonationassistant/events/history/event/HistoryItemEvent.java) |
| STOMP-клиент ODA | [socket.ts, c8184d0](https://github.com/OpenDonationAssistant/oda-widget-page/blob/c8184d0e409e9a90bf4a94b3a64a139c9db0a81a/src/socket.ts) |
| Значения system импортированных сервисов | [HistoryItemRepository.java, 75fd18e](https://github.com/OpenDonationAssistant/oda-history-service/blob/75fd18e61cdb67fe3fa0614c2589e43d2755c506/src/main/java/io/github/opendonationassistant/history/repository/HistoryItemRepository.java) |
| Фреймы, escaping, content-length и ACK | [STOMP 1.2](https://stomp.github.io/stomp-specification-1.2.html) |
| Exchange destination и свойства очереди | [RabbitMQ STOMP](https://www.rabbitmq.com/docs/stomp#exchange-destinations) |

Приложение ODA использует Authorization Code/PKCE S256; production token
endpoint — `https://auth.oda.digital/realms/ODA/protocol/openid-connect/token`.
Запрос использует scopes `stomp offline_access`, API — `https://api.oda.digital/`,
WebSocket — `wss://api.oda.digital/ws`. Из publisher routing key subscriberId
и RabbitMQ contract выведен маршрут `/exchange/subscriptions/{clientId}`.
Права доступа и ограничения конкретного
OAuth-приложения не определяются одним описанием протокола.

Установка `exclusive=false` и `auto-delete=false` нужна для сохранения
exchange-очереди при отключении клиента. CONNECT host выбирает virtual host,
поэтому DNS-имя WebSocket не подставляется как vhost. Manual ACK выполняется
только после durable commit; prefetch=1 уменьшает окно неопределённости.
TTL 24 часа не означает неограниченную историю или гарантию отсутствия
потерь при квотах брокера.

Публичные контракты не определяют права конкретного OAuth-приложения
и поведение выбранной установки RutonyChat. Настройка и проверка своей
установки описаны в [инструкции](../oda-setup.md). Мост использует
`SayToWindow` для HTML-ссылки на собственную панель; отображение ссылки
зависит от темы RutonyChat, private UI layout не модифицируется.
