# E-Commerce Microservices Platform (.NET 10)

Мікросервісна платформа електронної комерції на базі **.NET 10 (C# 14)** з єдиною точкою входу **API Gateway (Ocelot)**, декларативною моделлю авторизації **PBAC (Permission-Based Access Control)**, синхронною міжсервісною взаємодією через **gRPC**, агрегацією даних (BFF Composed Screen), кешуванням та Rate Limiting.

---

## 1. Архітектура системи

| Сервіс | Порт (REST / gRPC) | База даних (PostgreSQL) | Відповідальність |
| :--- | :--- | :--- | :--- |
| **ApiGateway** | `5000` | — (In-Memory Cache) | Ocelot-маршрутизація, JWT-валідація та збагачення заголовків (`X-User-*`), складений екран (`BFF`), кешування з інвалідизацією, Rate Limiting, наскрізний `X-Correlation-Id`. |
| **AuthService** | `5001` | `EcommerceAuthDb` | Реєстрація, автентифікація (BCrypt), випуск JWT та ротація Refresh Token, управління користувачами, ролями та PBAC-дозволами. |
| **CatalogService** | `5002` / `5102` (gRPC) | `EcommerceCatalogDb` | Публічна вітрина товарів і категорій (пошук, фільтрація, пагінація), адмінський CRUD, gRPC-сервіс перевірки, резервування та повернення залишків (`ReserveStock` / `ReleaseStock`). |
| **OrderingService** | `5003` | `EcommerceOrdersDb` | Оформлення замовлень із підтримкою ідемпотентності (`Idempotency-Key`), резервування товарів через gRPC із компенсуючим відкатом, кінцевий автомат статусів замовлення (FSM). |
| **ECommerce.Shared** | Library | — | Спільні контракти `catalog.proto`, матриця дозволів PBAC (`Permissions`, `[HasPermission]`), `GlobalExceptionHandler` (RFC 7807 `ProblemDetails`), конфігурація JWT. |

---

## 2. Структура репозиторію

```text
.
├── ECommerce.sln
├── docker-compose.yml
├── shared/
│   └── protos/
│       └── catalog.proto
├── src/
│   ├── ApiGateway/
│   ├── AuthService/
│   ├── CatalogService/
│   ├── OrderingService/
│   └── Shared/
└── tests/
    ├── ApiGateway.IntegrationTests/
    ├── AuthService.IntegrationTests/
    ├── CatalogService.IntegrationTests/
    └── Ordering.IntegrationTests/
```

---

## 3. Ключові інженерні рішення

- **Permission-Based Access Control (PBAC):** Контролери перевіряють атомарні права (`catalog:manage`, `orders:view-all`, `orders:manage-status`, `users:manage-roles`, `system:manage-cache`) через атрибут `[HasPermission(...)]`, не прив'язуючись до назв ролей у коді.
- **Синхронний gRPC + компенсуюча логіка:** Під час створення замовлення `OrderingService` одним gRPC-викликом `ReserveStock` перевіряє наявність, резервує залишок і отримує актуальний знімок цін; у разі збою збереження або скасування замовлення викликається `ReleaseStock`.
- **Ідемпотентність замовлень:** Повторні `POST /api/v1/orders` з однаковим заголовком `Idempotency-Key` повертають збережений результат із заголовком `Idempotency-Replayed: true` без повторного списання залишків.
- **BFF-агрегація та Graceful Degradation:** Ендпоінт `GET /api/v1/screens/item-details/{id}` на шлюзі паралельно опитує `CatalogService` (з кешуванням спільних даних) та `OrderingService` (персональні дані без кешування). При падінні `OrderingService` шлюз повертає `200 OK` з частковою відповіддю (`ActiveOperationStatus = "Unavailable"`).
- **Єдиний формат помилок (RFC 7807 ProblemDetails):** Всі сервіси та шлюз повертають структуровані `ProblemDetails` та `ValidationProblemDetails` зі збереженням `X-Correlation-Id`.

---

## 4. Запуск та тестування

### Запуск у Docker Compose
```bash
docker compose up --build -d
```

- **API Gateway & Swagger UI:** `http://localhost:5000/swagger`
- **Health Check (агрегований):** `http://localhost:5000/health`

### Запуск інтеграційних тестів
Усі 4 тестові проєкти використовують `WebApplicationFactory<Program>` та ізольовані бази даних у пам'яті:
```bash
dotnet test ECommerce.sln
```
