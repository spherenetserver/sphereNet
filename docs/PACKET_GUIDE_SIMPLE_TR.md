# SphereNet Paket Sistemi: Basit Anlatım Rehberi

Bu rehber, SphereNet içindeki ağ paketlerinin (network packets) ne işe yaradığını, nerede bulunduğunu ve Source-X mantığıyla nasıl eşleştiğini en basit haliyle açıklar.

## 1. Paket Nedir? (En Basit Haliyle)
Ultima Online'da oyuncunun yaptığı her hareket (tıklama, yürüme, konuşma) sunucuya bir "paket" olarak gider. Sunucu da buna karşılık oyuncuya "şunu gör", "şu kadar canın kaldı" gibi bilgiler içeren paketler gönderir.

- **Incoming (Gelen):** Oyuncudan sunucuya (İstekler).
- **Outgoing (Giden):** Sunucudan oyuncuya (Görüntü/Bilgi).

---

## 2. Gelen Paketler (Oyuncu Ne Yaptı?)
Oyuncunun yaptığı bir eylem sunucuya geldiğinde `SphereNet.Network/Packets/Incoming/` klasöründeki sınıflar tarafından okunur.

| Paket Adı | Opcode | Ne Zaman Çalışır? | SphereNet'te Nereyi Tetikler? |
|---|---|---|---|
| **PacketMovement** | `0x02` | Oyuncu yürümeye çalıştığında. | `GameClient.OnMovement` |
| **PacketSpeech** | `0x03` | Oyuncu bir şey yazdığında. | `GameClient.OnSpeech` |
| **PacketDoubleClick**| `0x06` | Bir eşyaya/kapıya çift tıklandığında. | `GameClient.OnDoubleClick` |
| **PacketItemPickup** | `0x07` | Yerden bir eşya kaldırıldığında. | `GameClient.OnItemPickup` |
| **PacketSingleClick**| `0x09` | Bir eşyaya tek tıklandığında (ismini görmek için).| `GameClient.OnSingleClick` |
| **PacketTarget** | `0x6C` | Target (imleç) ile bir yere tıklandığında. | `GameClient.OnTargetResponse` |

---

## 3. Giden Paketler (Oyuncu Ne Görüyor?)
Sunucu oyuncuya bir şey göstermek istediğinde `SphereNet.Network/Packets/Outgoing/` klasöründeki sınıfları kullanır.

| Paket Adı | Opcode | Ne İşe Yarar? | Nereden Çağrılır? |
|---|---|---|---|
| **PacketSendMessage**| `0x1C` | Oyuncuya yazı/mesaj gönderir. | `obj.SysMessage("Merhaba")` |
| **PacketDeleteObj** | `0x1D` | Bir eşyayı oyuncunun ekranından siler. | `obj.RemoveFromView()` |
| **PacketDrawPlayer** | `0x20` | Oyuncunun kendi karakterini ekranda çizer. | Oyuna ilk girişte otomatik. |
| **PacketUpdateStat** | `0x11` | Can (HP), Mana, Stamina bilgilerini günceller. | `mobile.UpdateStats()` |
| **PacketOpenGump** | `0xB0` | Ekranda bir menü (gump) açar. | `mobile.SendGump(new MyGump())` |

---

## 4. Kod İçinde "Nerede, Ne Var?" (Hızlı Referans)

### Paketin Uzunluğunu Kim Belirliyor?
- **Dosya:** `src/SphereNet.Network/Packets/PacketDefinitions.cs`
- **Ne işe yarar?** Hangi paketin kaç byte olduğunu (Source-X'teki `g_Packet_Lengths` tablosu gibi) burada görebilirsiniz.

### Yeni Bir Paket Nasıl Gönderilir?
Bir oyuncuya (client) paket göndermek için şu kalıbı kullanırız:
```csharp
client.Send(new PacketRelay(ip, port, authId));
```

### Yeni Bir Gelen Paket Nasıl Eklenir?
1. `Incoming/` altına yeni bir dosya aç (Örn: `PacketMyCustom.cs`).
2. `PacketHandler`'dan türet.
3. `OnReceive` içinde `buffer.ReadByte()` vb. ile veriyi oku.
4. `state.OnMyAction(...)` diyerek oyun mantığına (Game Logic) aktar.

---

## 5. Eksikleri Nasıl Takip Ederim? (Checklist)

Eğer "şu özellik neden çalışmıyor?" diyorsanız şu iki dosyaya bakın:

1.  **[PROTOCOL_MATRIX.md](PROTOCOL_MATRIX.md)**: Burada "Mandatory Implemented" (Zorunlu ve Yapıldı) listesinde olmayan paketler henüz sunucuda tam işlenmiyor demektir.
2.  **[PARITY.md](PARITY.md)**: Burada "Open" veya "Partial" yazan yerler, Source-X ile henüz %100 aynı davranmayan özelliklerdir.

### Özetle İş Akışı:
`Paket Gelir (Network)` -> `Handler Okur (Incoming)` -> `NetState/GameClient İşler (Logic)` -> `Sonuç Oyuncuya Gider (Outgoing)`
