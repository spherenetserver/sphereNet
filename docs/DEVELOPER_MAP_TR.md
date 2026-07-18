# SphereNet Geliştirici Haritası: Paketlerin Yolculuğu

Bu döküman, Source-X'ten gelen bir geliştiricinin SphereNet mimarisini "bir bakışta"
anlaması ve kodun içinde kaybolmaması için hazırlanmış rehberdir. (Eski
`TRANSITION_GUIDE_TR.md` bu dosyaya birleştirildi, 2026-07-18.)

---

## 1. Temel Felsefe: Source-X vs SphereNet

Source-X'te her şey `CClient` içindeki devasa dosyalarda (`CClientEvent.cpp` vb.)
dönerken, SphereNet işleri parçalara böler:

- **Ağ Katmanı (Network):** Sadece veriyi okur/yazar. Oyun kuralı bilmez.
- **Köprü (Bridge):** Ağdan gelen veriyi oyun motoruna bağlar.
- **Oyun Mantığı (Game Logic):** Karakterin ne yapacağına karar verir.

## 2. Mimari Eşleşmeler (Source-X → SphereNet)

| Source-X Sınıfı / Dosyası | SphereNet Karşılığı | Konum (src/) |
|---|---|---|
| `CObjBase` | `ObjBase` | `SphereNet.Game/Objects/ObjBase.cs` |
| `CChar` | `Character` | `SphereNet.Game/Objects/Characters/` |
| `CItem` | `Item` | `SphereNet.Game/Objects/Items/` |
| `CClient` | `GameClient` + `Client*Handler` sınıfları | `SphereNet.Game/Clients/` |
| `CServer` | `Engine` (Wiring) | `SphereNet.Server/Program.EngineWiring.cs` |
| `CWorld` | `GameWorld` | `SphereNet.Game/World/` |
| `CScript` | `ScriptFile` / `ScriptSection` | `SphereNet.Scripting/Parsing/` |
| `Packet` | `PacketBase` / `PacketBuffer` | `SphereNet.Network/Packets/` |
| `receive.cpp` | `NetworkManager` / `Packets/Incoming/` | `SphereNet.Network/Manager/` |
| `send.cpp` | `Packets/Outgoing/` | `SphereNet.Network/Packets/Outgoing/` |

## 3. Bir Paketin Yolculuğu (Gelen Paket - Incoming)

Oyuncu bir şeye tıkladığında ne olur?

1.  **Soket:** `NetworkManager.cs` veriyi yakalar.
2.  **Ayrıştırıcı (Parser):** `Incoming/GamePackets.cs` içindeki ilgili sınıf
    (örn. `PacketDoubleClick`) veriyi byte byte okur.
3.  **Haberci (Delegate):** Okunan veri `NetState` içindeki bir "olay"ı (event) tetikler.
4.  **Bağlantı (Wiring):** `Program.NetworkHandlers.cs` bu olayı yakalar ve
    "Hey `GameClient`, oyuncu şu UID'ye çift tıkladı!" der.
5.  **Davranış (Behavior):** `GameClient.HandleDoubleClick` →
    `ClientItemUseHandler.HandleDoubleClick` çalışır ve eşyayı açar/kullanır.

Kısa yol: `PacketHandler (Network)` → `NetState (Bridge)` → `GameClient (Session)`
→ `World/Object (Game Logic)`.

## 4. Kod Haritası: "Neyi Nerede Bulurum?"

| Aradığın Şey | Klasör / Dosya Yolu | Source-X Karşılığı |
|---|---|---|
| **Paket Tanımları** | `src/SphereNet.Network/Packets/PacketDefinitions.cs` | `g_Packet_Lengths` tablosu |
| **Gelen Paketler** | `src/SphereNet.Network/Packets/Incoming/` | `receive.cpp` içindeki case'ler |
| **Giden Paketler** | `src/SphereNet.Network/Packets/Outgoing/` | `send.cpp` metodları |
| **Ağ-Oyun Köprüsü** | `src/SphereNet.Server/Program.NetworkHandlers.cs` | `CClient::OnEvent` |
| **Karakter Davranışı** | `src/SphereNet.Game/Clients/` (aşağıya bak) | `CClient.cpp`, `CClientEvent.cpp` |
| **Eşya Davranışı** | `src/SphereNet.Game/Objects/Items/Item.cs` | `CItem.cpp` |
| **Trigger Sistemi** | `src/SphereNet.Game/Scripting/TriggerDispatcher.cs` + `src/SphereNet.Scripting/Execution/TriggerRunner.cs` | `OnTrigger` metodları |
| **Skill Sistemi** | `src/SphereNet.Game/Skills/` (`ActiveSkillEngine`) | `CCharSkill.cpp` |
| **Combat** | `src/SphereNet.Game/Combat/` (SwingEngine vb.) | `CCharFight.cpp` |

## 5. GameClient'ın Yapısı (Decomposition Sonrası)

`GameClient` iki katmandan oluşur (Wave 88-94 decomposition'ı):

- **İnce partial'lar** (`GameClient.*.cs` — Login, Inventory, Combat, ItemUse,
  ViewUpdate, Targeting, Dialogs, Skills, Housing, Mail, Chat, Context, ...):
  çoğunlukla delegasyon; dışa açık API burada durur.
- **Handler sınıfları** (`Client*Handler.cs`): gerçek davranış buraya taşındı —
  `ClientItemUseHandler` (dclick/kullanım), `ClientInventoryHandler` (pickup/drop/equip),
  `ClientCombatHandler` (savaş/tick), `ClientTargetingHandler`, `ClientViewUpdater`
  (view delta build/apply), `ClientSkillsHandler` (skill + AOS tooltip),
  `ClientWorldFeaturesHandler` (kapılar, context menü, housing gump'ları),
  `ClientDialogHandler`, `ClientScriptConsoleHandler`. Ortak durum:
  `ClientViewCache`, `ClientTargetState`; erişim dikişi: `IClientContext`.

Bir davranışı ararken önce partial'daki delegasyona, oradan handler sınıfına in.

## 6. Örnek Senaryo: Eşyayı Yere Bırakma (Item Drop)

1.  **Okuma:** `PacketItemDrop.OnReceive` (0x08) koordinatları okur.
2.  **Köprü:** `Program.NetworkHandlers` → `OnItemDrop` tetiklenir.
3.  **Mantık:** `ClientInventoryHandler.HandleItemDrop` çalışır.
    - Eşya çantadan çıkarılır.
    - `World` üzerine (haritaya) yerleştirilir.
4.  **Geri Bildirim:** `PacketDropAck` (0x29) ile işlem onaylanır.
5.  **Görsel:** `ClientViewUpdater` devreye girer ve etraftaki herkese
    "Burada yeni bir eşya var" der (`0x1A` veya `0xF3` paketi).

## 7. Eksikleri Takip Etme ve Test Etme

1.  **Protocol Matrix:** `docs/PROTOCOL_MATRIX.md` içindeki "Deferred" veya
    listelenmemiş opcode'lar üzerinde çalışabilirsiniz.
2.  **Parity Matrix:** `docs/PARITY.md` içindeki "Partial" veya "Open" alanlar
    önceliklidir; ertelenen kuyruk aynı dosyanın "Open threads" bölümünde.
3.  **Sessiz stub'lar:** `docs/STUB_INVENTORY_TR.md` — tanımlı görünüp no-op olan yüzeyler.
4.  **TODO Yorumları:** Kod içinde `// TODO:` araması.
5.  **Testler:** SphereNet test güdümlüdür; yeni özellik = `src/SphereNet.Tests/`
    altında test. Örnek desenler: `GameSystemTests`, `TriggerCoverageGuardrailTests`,
    `ScriptObjectParityTests`.

Eksik bulma örneği ("yere eşya atınca ses çıkmıyor"): giden paket eksiği →
`Outgoing/`'da `PacketSound` var mı? → davranışın sahibine git
(`ClientInventoryHandler.HandleItemDrop`) → sonuna `BroadcastNearby(new PacketSound(...))` ekle.

## 8. Yeni Bir Özellik Eklerken İzlenecek Yol

1.  **Paketi Tanımla:** `Incoming` içinde parser'ı yaz (`PacketHandler`'dan türet,
    `OnReceive` override et, `state.OnSomething(...)` ile oyuna aktar).
2.  **Kaydet:** `NetworkManager.RegisterStandardPackets` içine ekle.
3.  **Köprü Kur:** `NetState` ve `Program.NetworkHandlers` içinde bağlantıyı yap.
4.  **Mantığı Yaz:** İlgili `Client*Handler` içinde ne yapacağını kodla.
5.  **Test Et:** `SphereNet.Tests` altında test yazarak çalıştığından emin ol.
