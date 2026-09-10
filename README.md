# 🛒 Distributed Order Platform (Microservices & Event-Driven Architecture)

> **ASP.NET Core (.NET 10)** ile geliştirilmiş, **MassTransit & RabbitMQ** ile asenkron olay güdümlü (event-driven) haberleşen, **Database-per-Service** prensibiyle her servisin bağımsız PostgreSQL veritabanına sahip olduğu dağıtık sipariş yönetim platformu.

---

## 🎯 Projenin Amacı ve Mimari Felsefesi

Bu proje; monolitik mimarilerin getirdiği sıkı bağımlılıkları (tight coupling) kırmak, servisleri birbirinden tamamen bağımsız olarak ölçeklenebilir ve deploy edilebilir kılmak amacıyla tasarlanmıştır.

### 🔑 Temel İlkeler ve Kararlar:
* **Database-per-Service:** `Order.Service` ve `Payment.Service` tamamen fiziksel olarak ayrı PostgreSQL container'larına (`order-db:5433` ve `payment-db:5434`) bağlanır. İki servis arasında asla SQL `JOIN` veya fiziksel Foreign Key constraint kullanılmaz.
* **Loose Coupling (Gevşek Bağlılık):** Servisler birbirine doğrudan HTTP isteği atmaz (Senkron çağrı yok). İletişim RabbitMQ üzerinden asenkron event'ler ile sağlanır.
* **Resilience (Dayanıklılık):** `Payment.Service` kapalı olsa bile siparişler kesintiye uğramadan `order-db`'ye kaydedilir ve mesajlar RabbitMQ kuyruğunda güvenle bekletilir.
* **MassTransit Abstraction:** RabbitMQ üzerindeki Exchange/Queue topolojisi, serileştirme ve hata yönetimi MassTransit ile otomatik yönetilir.

---

## 🏛️ Mimari Akış Şeması

```mermaid
flowchart LR
    Client([💻 İstemci / Swagger]) -->|POST /orders| OS[Order.Service<br/>:5001]
    
    subgraph Order Domain
        OS -->|Yazar / Okur| ODB[(PostgreSQL<br/>order-db :5433)]
    end

    OS -->|Publish: OrderCreated| RMQ{{RabbitMQ Message Broker<br/>:5672}}

    subgraph Payment Domain
        RMQ -->|Consume: OrderCreated| PS[Payment.Service<br/>:5002]
        PS -->|Yazar / Okur| PDB[(PostgreSQL<br/>payment-db :5434)]
    end

    classDef service fill:#2d3748,stroke:#4a5568,stroke-width:2px,color:#fff;
    classDef broker fill:#d97706,stroke:#b45309,stroke-width:2px,color:#fff;
    classDef db fill:#2563eb,stroke:#1d4ed8,stroke-width:2px,color:#fff;

    class OS,PS service;
    class RMQ broker;
    class ODB,PDB db;
```

---

## 🛠️ Teknoloji Yığını (Tech Stack)

| Alan | Teknoloji | Açıklama |
| :--- | :--- | :--- |
| **Framework** | .NET 10 (ASP.NET Core Web API) | En güncel C# ve Minimal API mimarisi |
| **Message Broker** | RabbitMQ 3 (Management Image) | Servisler arası asenkron mesajlaşma |
| **Service Bus** | MassTransit (v8.3) | Abstraction, otomatik topoloji ve consumer altyapısı |
| **ORM / Veri** | Entity Framework Core & Npgsql | Code-first yaklaşımı ve PostgreSQL entegrasyonu |
| **Veritabanları** | PostgreSQL 16 (Docker) | Her servis için izole container'lar |
| **Konteynerleştirme**| Docker & Docker Compose | Tek komutla altyapı orkestrasyonu |
| **Dokümantasyon** | Swagger / OpenAPI | Canlı test arayüzü |

---

## 📂 Çözüm Yapısı

```
order-platform-learning/
├── docker-compose.yml              # RabbitMQ (5672/15672), order-db (5433), payment-db (5434)
├── OrderPlatform.sln               # Tüm servisleri bağlayan ana çözüm dosyası
│
├── Shared.Contracts/               # Ortak Event ve Mesaj Tipleri
│   └── Events/
│       └── OrderCreated.cs         # OrderId, ProductName, Quantity, TotalPrice, CreatedAt
│
├── Order.Service/                  # Sipariş Kabul ve Yönetim Servisi (Port: 5001)
│   ├── Data/OrderDbContext.cs      # EF Core Postgres context
│   ├── Models/Order.cs             # Sipariş entity'si
│   ├── DTOs/OrderDtos.cs           # Request / Response modelleri
│   └── Program.cs                  # MassTransit PublishEndpoint entegrasyonu
│
└── Payment.Service/                # Ödeme İşleme Servisi (Port: 5002)
    ├── Consumers/
    │   └── OrderCreatedConsumer.cs # RabbitMQ'dan OrderCreated dinleyip ödemeyi işler
    ├── Data/PaymentDbContext.cs    # EF Core Postgres context
    ├── Models/Payment.cs           # Ödeme entity'si (Mantıksal OrderId referansı)
    └── Program.cs                  # MassTransit Consumer kaydı & ConfigureEndpoints
```

---

## ⚡ Hızlı Başlangıç (Nasıl Çalıştırılır?)

### 1. Gereksinimler
* [.NET 10 SDK](https://dotnet.microsoft.com/)
* [Docker Desktop](https://www.docker.com/)

### 2. Altyapıyı Başlat (Docker)
Proje kök dizininde RabbitMQ ve 2 ayrı PostgreSQL veritabanını ayağa kaldırın:
```bash
docker compose up -d
```
> RabbitMQ Yönetim Paneli: [http://localhost:15672](http://localhost:15672) *(Kullanıcı/Şifre: guest / guest)*

### 3. Servisleri Çalıştır

İki ayrı terminal açarak servisleri başlatın:

**Terminal 1 — Order.Service:**
```bash
cd Order.Service
dotnet run
```
*API & Swagger:* [http://localhost:5001/swagger](http://localhost:5001/swagger)

**Terminal 2 — Payment.Service:**
```bash
cd Payment.Service
dotnet run
```
*API & Swagger:* [http://localhost:5002/swagger](http://localhost:5002/swagger)

---

## 🧪 Canlı Test ve Senaryo Doğrulama

1. **Sipariş Oluştur (POST):**
   ```bash
   curl -X POST http://localhost:5001/orders \
     -H "Content-Type: application/json" \
     -d '{"productName": "MacBook Pro M3", "quantity": 1, "totalPrice": 2499.99}'
   ```
2. **RabbitMQ & Payment.Service Logunu İncele:**
   `Payment.Service` konsolunda event'in yakalandığını gözlemleyin:
   ```text
   📬 [RabbitMQ] OrderCreated Event Alındı! OrderId: 1f... Ürün: MacBook Pro M3, Tutar: $2,499.99
   ✅ [Ödeme Başarılı] PaymentId: ... için ödeme payment-db'ye kaydedildi.
   ```
3. **Ödemeleri Listele (GET):**
   ```bash
   curl http://localhost:5002/payments
   ```

---

## 🗺️ Yol Haritası (Roadmap)

- [x] **Faz 1:** Docker altyapısı (RabbitMQ + PostgreSQL instances).
- [x] **Faz 2:** `Order.Service` oluşturulması ve EF Core PostgreSQL CRUD.
- [x] **Faz 3:** `Shared.Contracts` ve MassTransit ile `OrderCreated` event publish mekanizması.
- [x] **Faz 4:** `Payment.Service` ve `OrderCreatedConsumer` ile bağımsız ödeme işleme.
- [ ] **Faz 5:** `Notification.Service` eklenmesi (`PaymentProcessed` veya `OrderCreated` dinleyicisi).
- [ ] **Faz 6:** YARP (Yet Another Reverse Proxy) API Gateway entegrasyonu (Dış dünyaya tek kapı).
- [ ] **Faz 7:** Tüm servislerin Dockerfile ve tek tıkla `docker-compose up` ile ayağa kaldırılması.
- [ ] **Faz 8:** Gözlemlenebilirlik (Health Checks, OpenTelemetry / Serilog + Seq).

---

## 👩‍💻 Geliştirici
**Melike Arslan** — [GitHub Profilim](https://github.com/melikee46)
