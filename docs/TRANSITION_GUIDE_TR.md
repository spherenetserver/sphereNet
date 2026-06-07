# Source-X'ten SphereNet'e Geçiş Rehberi

Bu döküman, Source-X (C++) tabanlı Sphere geliştiricilerinin SphereNet (C# / .NET) mimarisini anlamasını ve geliştirmelere katkıda bulunmasını kolaylaştırmak için hazırlanmıştır.

## 1. Mimari Eşleşmeler (Dosya Yapısı)

Source-X'teki tanıdık sınıfların SphereNet'teki karşılıkları aşağıdadır:

| Source-X Sınıfı / Dosyası | SphereNet Karşılığı | Konum (src/) |
|---|---|---|
| `CObjBase` | `ObjBase` | `SphereNet.Game/Objects/ObjBase.cs` |
| `CChar` | `Player` / `Mobile` | `SphereNet.Game/Objects/Characters/` |
| `CItem` | `Item` | `SphereNet.Game/Objects/Items/` |
| `CClient` | `GameClient` | `SphereNet.Game/Clients/GameClient.cs` |
| `CServer` | `Engine` (Wiring) | `SphereNet.Server/Program.EngineWiring.cs` |
| `CWorld` | `World` | `SphereNet.Game/World/` |
| `CScript` | `ScriptFile` / `ScriptSection` | `SphereNet.Scripting/Parsing/` |
| `Packet` | `PacketBase` / `PacketBuffer` | `SphereNet.Network/Packets/` |
| `receive.cpp` | `NetworkManager` / `PacketHandlers` | `SphereNet.Network/Manager/` & `Packets/Incoming/` |

## 2. Packet (Paket) Yapısı ve İşleme

Source-X'te paketler genellikle `CClientEvent.cpp` içinde `Event_*` metodlarıyla işlenir. SphereNet'te ise her paket için ayrı bir sınıf bulunur.

### Örnek: Çift Tıklama (Double Click - 0x06)
- **Source-X**: `CClient::Event_DoubleClick`
- **SphereNet**: `PacketDoubleClick` sınıfı (`SphereNet.Network/Packets/Incoming/GamePackets.cs`)

### Paket Ekleme/Takip Etme
Yeni bir paket eklemek için:
1. `SphereNet.Network/Packets/Incoming/` altında ilgili paket sınıfını oluşturun.
2. `PacketHandler` sınıfından türetin.
3. `OnReceive` metodunu override ederek veriyi okuyun.
4. `state.OnSomething(...)` diyerek `NetState` (veya `GameClient`) üzerinden oyuna aktarın.

**Eksik Paketler**: Mevcut durum için `docs/PROTOCOL_MATRIX.md` dosyasını inceleyin. "Mandatory Implemented", "Optional Implemented" ve "Known Ignored" listeleri burada tutulur.

## 3. Komponent ve Trigger Sistemi

SphereNet, Source-X'e göre daha modüler bir yapıdadır.

- **Triggerlar**: `SphereNet.Game/Scripting/TriggerRunner.cs` içinde yönetilir. `@Hit`, `@Death`, `@GetHit` gibi klasik triggerlar burada dispatch edilir.
- **Skill Sistemi**: `SphereNet.Game/Skills/` altında `ActiveSkillEngine` tarafından yönetilir.
- **Combat**: `SphereNet.Game/Combat/` altındaki motorlar (SwingEngine vb.) tarafından yürütülür.

## 4. Scripting Parity (Uyumluluk)

Eski scriptlerinizin (`.scp`) SphereNet'te nasıl çalıştığını anlamak için `docs/PARITY.md` dosyasını kontrol edin. 
- `LOCAL.*`, `SERV.*`, `UID.*` gibi erişimlerin durumu burada güncel olarak tutulur.
- Eğer bir özellik eksikse, genellikle `SphereNet.Scripting/Execution/ScriptInterpreter.cs` içinde bir karşılığı henüz yazılmamıştır.

## 5. Eksikleri Takip Etme ve Test Etme

Geliştirme yaparken eksikleri şu şekilde takip edebilirsiniz:

1.  **Protocol Matrix**: `docs/PROTOCOL_MATRIX.md` içindeki "Deferred" veya listelenmemiş opcode'lar üzerinde çalışabilirsiniz.
2.  **Parity Matrix**: `docs/PARITY.md` içindeki "Partial" veya "Open" alanlar önceliklidir.
3.  **TODO Yorumları**: Kod içinde `// TODO:` araması yaparak yapılması planlanan işleri görebilirsiniz.
4.  **Testler**: SphereNet "Test-Driven" bir yaklaşıma sahiptir. Yeni bir özellik eklediğinizde `tests/` altında bir unit test eklemeniz beklenir. Mevcut testleri inceleyerek (örn: `SphereNet.Game.Tests`) nasıl test yazılacağını görebilirsiniz.

## 6. Hızlı Başlangıç İpucu

Source-X'ten gelen bir geliştirici olarak, bir paketin oyun dünyasında ne yaptığını bulmak için şu yolu izleyin:
`PacketHandler (Network)` -> `NetState (Bridge)` -> `GameClient (Session)` -> `World/Object (Game Logic)`

Örneğin:
1. `PacketItemPickup` (0x07) veriyi okur.
2. `state.OnItemPickup` metodunu çağırır.
3. `GameClient` bu isteği doğrular ve `World` üzerindeki nesneyi hareket ettirir.
