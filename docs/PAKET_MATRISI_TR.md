# Paket matrisi — sınıf sayısı yanlış ölçüdür

PLAN-603: *"Paketleri sınıf sayısı yerine opcode/subcommand/version/length/read/
write matrisiyle ölç. BondedStatus vb. isim eksiklerini başka paketle eşdeğer
karşılanıp karşılanmadığı açısından incele."*

## Neden sınıf saymak yanıltıyor

Durum raporu paket katmanını "109 giden paket sınıfı (SX 124)" diye ölçüyordu.
Bu ölçü iki yönde birden yanılır:

- **Eksik gösterir:** referansta ayrı sınıf olan bir şey bizde başka bir sınıfın
  içinde karşılanıyor olabilir.
- **Var gösterir:** bir opcode için sınıfımız olabilir ama o opcode'un
  **alt-alt-komutlarından** biri hiç yazılmamış olabilir.

`0xBF 0x19` ikinci durumun canlı örneği ve bu dilimde bulunan gerçek boşluk.

## 0xBF 0x19 — tek alt-komut, dört mesaj

Referansta aynı `0x19` değeri **dört** isim taşıyor (`sphereproto.h:243-246`):

| Ayrım | Anlamı | Bizde |
|---|---|---|
| `0x19` + tip `0x00` | **BondedStatus** — yaratık bonded | **yoktu → eklendi** |
| `0x19` + tip `0x02` | Stats_Enable — stat kilitleri | vardı (`PacketStatLockInfo`) |
| `0x19` + `0x05.0xff` | NewBondedStatus | referansın kendisi "gerçekten var mı?" diye yorumlamış |
| `0x19` | StatueAnimation | ikisi de yok |

Sınıf envanteri `0x19`'u "var" sayıp geçerdi. Oysa bonded bir evcil hayvanın
hayaleti izleyen hiç kimseye **bonded olarak bildirilmiyordu** — ve bu motorda
bonded pet'ler ölünce gerçekten hayalet olarak dünyada kalıyor (İŞ-31).

Referansın iki gönderim noktası portlandı:
- karakter çizilirken, NPC + bonded + ölü ise `isGhost=1` (`CClientMsg.cpp:1201`)
- diriltmede, gören her istemciye `isGhost=0` (`CCharSpell.cpp:484`)

## 0xBF alt-komut kapsaması

Referans **35 ayrı** `EXTDATA_*` değeri tanımlıyor (38 isim; `0x19` dördü birden).

| Yön | Bizde | Değerler |
|---|---:|---|
| **Gelen** (istemciden) | 16 | 05 06 07 09 0A 0B 13 15 1A 1C 1E 24 2C 2E 32 33 |
| **Giden** (sunucudan) | 14 | 01 02 04 06 08 16 17 18 19 1B 1D 20 22 26 |

Aynı alt-komutu birden çok sınıfın yazması normaldir ve sınıf saymanın neden
yanıltıcı olduğunun bir başka yüzüdür: `0x06` tek başına dört sınıf
(`PartyInvitation`, `PartyMemberList`, `PartyMessage`, `PartyRemoveMember`).

## Bu matris nasıl kullanılır

Bir paket "eksik" denmeden önce üç soru:

1. **Opcode var mı?** Yoksa gerçek eksik.
2. **Alt-komut var mı?** Opcode varken alt-komut yoksa sınıf envanteri bunu
   göremez — `0x19`'daki gibi.
3. **Başka bir paket eşdeğerini karşılıyor mu?** Karşılıyorsa eksik değil,
   bilinçli bir sapmadır ve kayda geçmelidir.

`BondedStatusPacketParityTests` `0x19`'un iki yarısını (tip 0x00 ve 0x02) birlikte
sabitliyor: birini tek başına sınayan bir test, ikisinin çakışmasını yakalayamaz.
