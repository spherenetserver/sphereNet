# SphereNet Geliştirici Haritası: Paketlerin Yolculuğu

Bu döküman, Source-X'ten gelen bir geliştiricinin SphereNet mimarisini "bir bakışta" anlaması ve kodun içinde kaybolmaması için hazırlanmış en kapsamlı rehberdir.

---

## 1. Temel Felsefe: Source-X vs SphereNet

Source-X'te her şey `CClient` içindeki devasa dosyalarda (`CClientEvent.cpp` vb.) dönerken, SphereNet işleri parçalara böler:

- **Ağ Katmanı (Network):** Sadece veriyi okur/yazar. Oyun kuralı bilmez.
- **Köprü (Bridge):** Ağdan gelen veriyi oyun motoruna bağlar.
- **Oyun Mantığı (Game Logic):** Karakterin ne yapacağına karar verir.

---

## 2. Bir Paketin Yolculuğu (Gelen Paket - Incoming)

Oyuncu bir şeye tıkladığında ne olur?

1.  **Soket:** `NetworkManager.cs` veriyi yakalar.
2.  **Ayrıştırıcı (Parser):** `Incoming/GamePackets.cs` içindeki ilgili sınıf (Örn: `PacketDoubleClick`) veriyi byte byte okur.
3.  **Haberci (Delegate):** Okunan veri `NetState` içindeki bir "olay"ı (event) tetikler.
4.  **Bağlantı (Wiring):** `Program.NetworkHandlers.cs` bu olayı yakalar ve "Hey `GameClient`, oyuncu şu UID'ye çift tıkladı!" der.
5.  **Davranış (Behavior):** `GameClient.ItemUse.cs` içindeki `HandleDoubleClick` metodu çalışır ve eşyayı açar/kullanır.

---

## 3. Kod Haritası: "Neyi Nerede Bulurum?"

SphereNet'te kodlar görevlerine göre klasörlenmiştir:

| Aradığın Şey | Klasör / Dosya Yolu | Source-X Karşılığı |
|---|---|---|
| **Paket Tanımları** | `src/SphereNet.Network/Packets/PacketDefinitions.cs` | `g_Packet_Lengths` tablosu |
| **Gelen Paketler** | `src/SphereNet.Network/Packets/Incoming/` | `receive.cpp` içindeki case'ler |
| **Giden Paketler** | `src/SphereNet.Network/Packets/Outgoing/` | `send.cpp` metodları |
| **Ağ-Oyun Köprüsü** | `src/SphereNet.Server/Program.NetworkHandlers.cs` | `CClient::OnEvent` |
| **Karakter Davranışı** | `src/SphereNet.Game/Clients/GameClient.*.cs` | `CClient.cpp`, `CClientEvent.cpp` |
| **Eşya Davranışı** | `src/SphereNet.Game/Objects/Items/Item.cs` | `CItem.cpp` |
| **Trigger Sistemi** | `src/SphereNet.Game/Scripting/TriggerDispatcher.cs` + `src/SphereNet.Scripting/Execution/TriggerRunner.cs` | `OnTrigger` metodları |

---

## 4. GameClient'ın Parçaları (Partial Classes)

`GameClient` sınıfı çok büyük olduğu için dosyalara bölünmüştür:

- `GameClient.Login.cs`: Oyuna giriş, karakter seçimi.
- `GameClient.Inventory.cs`: Eşya kaldırma (pickup), bırakma (drop), giyme (equip).
- `GameClient.Combat.cs`: Yürüme, savaşma, hedef alma.
- `GameClient.ItemUse.cs`: Çift tıklama, kapı açma, alet kullanma.
- `GameClient.ViewUpdate.cs`: Etraftaki eşyaların ekranda çizilmesi (Draw/Remove).
- `GameClient.PacketHelpers.cs`: Paket göndermeyi kolaylaştıran yardımcı metodlar.

---

## 5. Örnek Senaryo: Eşyayı Yere Bırakma (Item Drop)

Bir eşyayı yere bıraktığınızda kod şu sırayla akar:

1.  **Okuma:** `PacketItemDrop.OnReceive` (0x08) koordinatları okur.
2.  **Köprü:** `Program.NetworkHandlers` -> `OnItemDrop` tetiklenir.
3.  **Mantık:** `GameClient.Inventory.cs` -> `HandleItemDrop` çalışır.
    - Eşya çantadan çıkarılır.
    - `World` üzerine (haritaya) yerleştirilir.
4.  **Geri Bildirim:** `PacketDropAck` (0x29) ile işlem onaylanır.
5.  **Görsel:** `GameClient.ViewUpdate.cs` devreye girer ve etraftaki herkese "Burada yeni bir eşya var" der (`0x1A` veya `0xF3` paketi).

---

## 6. Geliştirici İçin "Eksik Bulma" Rehberi

Source-X'te olan bir şey SphereNet'te eksikse (Örneğin: "Yere eşya atınca ses çıkmıyor"):

1.  **Sorun Nerede?** Ses çıkmıyorsa bu bir "Giden Paket" (Outgoing) eksikliğidir.
2.  **Paket Var mı?** `Outgoing/ExtendedPackets.cs` içinde `PacketSound` sınıfı var mı? (Evet var).
3.  **Nereden Çağrılmalı?** Eşya yere düştüğünde olması gerektiği için `GameClient.Inventory.cs` içindeki `HandleItemDrop` metoduna git.
4.  **Ekleme Yap:** Metodun sonuna `BroadcastNearby(new PacketSound(0xXX))` ekle.

---

## 7. Yeni Bir Özellik Eklerken İzlenecek Yol

1.  **Paketi Tanımla:** `Incoming` içinde parser'ı yaz.
2.  **Kaydet:** `NetworkManager.RegisterStandardPackets` içine ekle.
3.  **Köprü Kur:** `NetState` ve `Program.NetworkHandlers` içinde bağlantıyı yap.
4.  **Mantığı Yaz:** `GameClient.*.cs` içinde ne yapacağını kodla.
5.  **Test Et:** `SphereNet.Tests` altında küçük bir test yazarak çalıştığından emin ol.
