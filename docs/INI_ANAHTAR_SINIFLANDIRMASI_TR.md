# Ini anahtar sınıflandırması

PLAN-002'nin istediği şey: *"her ini anahtarını `okunuyor / saklanıyor /
davranışta tüketiliyor / bilinçli desteklenmiyor` olarak sınıflandır."*

Sınıflandırmanın bir kısmı zaten vardı — `config/sphere.ini` her anahtarın
üstünde Türkçe bir durum işareti taşıyor (`[ÇALIŞIYOR]`, `[OKUNUYOR]`,
`[UYGULANMADI]`). Asıl soru işaretlerin **doğru** olup olmadığıydı. Değildi: 18 anahtarın işareti kodla
çelişiyordu; 15'i "uygulanmadı" diye işaretliyken tamamen çalışıyordu.

Bu belge ölçümün kendisini ve düzeltilen sapmaları kaydeder.
`IniKeyClassificationGuardrailTests` belgeyi ini'ye ve koda karşı sabitler.

---

## Ölçüm

`config/sphere.ini`: **195 anahtar** (bir yinelenen anahtar kaldırıldıktan
sonra; aşağıya bakın).

| Sınıf | Sayı | Anlamı |
|---|---|---|
| Davranışta tüketiliyor | **165** | Okunur, saklanır ve bir motor yolu değeri sorar |
| Script'ten okunabiliyor | **2** | `SERV.<anahtar>` cevaplar; motor davranışı yok |
| Okunuyor / saklanıyor | **11** | `SphereConfig`'e girer, kimse sormaz (10 kaza, 1 bilinçli no-op) |
| Bilinçli desteklenmiyor | **17** | Hiçbir yerde okunmaz |

Toplam 195.

Ölçüm `SphereConfig.cs`'in okuma çağrılarını çıkarır, her okumanın hangi
property'ye indiğini bulur, sonra o property'yi `SphereNet.Tests` dışındaki tüm
kaynakta arar. Host ve Panel bazı anahtarları `SphereConfig`'e uğramadan
doğrudan ini'den okur, bu yüzden arama tek dosyayla sınırlı değil.

### Ölçüm aracının göremedikleri

Üç okuma biçimi düz metin taramasıyla görünmez; hepsi elle doğrulandı ve
yukarıdaki sayılarda doğru tarafta duruyor:

| Anahtar | Neden görünmez | Gerçek |
|---|---|---|
| `MAP0`, `MAP1` | `$"Map{i}"` ile döngüde okunur | Tüketiliyor |
| `CLIENTERA`, `SEASONMODE`, `SAVEFORMAT` | Yerel değişkene okunup enum'a çevrilir | Tüketiliyor |
| `CHATFLAGS`, `GENERICSOUNDS` | Tek başvuru `SERV.*` yankısı | Davranış **yok** |

---

## Düzeltilen sapma 1 — 15 anahtar yanlışlıkla "uygulanmadı" diyordu

Bunların hepsinin işaretinde *"SphereConfig'de tanımlı değil"* yazıyordu.
Hepsi tanımlı, okunuyor ve bir motor yolundan tüketiliyor. İşaret, ayar
uygulanmadan önce yazılmış ve sonraki dalgalar ayarı uygularken ini'yi geride
bırakmış.

Operatör açısından bu sessiz bir kayıp: çalışan bir ayarın yanında "bu çalışmaz"
yazıyorsa, operatör onu hiç denemez.

| Anahtar | Tüketildiği yer |
|---|---|
| `MAPVIEWRADAR` | `Character.MapViewRadarTiles` |
| `COMBATPARRYINGERA` | `CombatEngine` |
| `COMBATARCHERYMOVEMENTDELAY` | `CombatHelper` |
| `ARCHERYMINDIST` | `NpcAI.Perception` |
| `ARCHERYMAXDIST` | `NpcAI.Perception` |
| `SPEEDSCALEFACTOR` | `Character.CombatSpeedScaleFactor` |
| `EQUIPPEDCAST` | `Character.EquippedCastEnabled` |
| `REAGENTLOSSABORT` | `SpellEngine` |
| `REAGENTLOSSFAIL` | `SpellEngine` |
| `MANALOSSABORT` | `SpellEngine` |
| `MANALOSSFAIL` | `SpellEngine` |
| `MANALOSSPERCENT` | `SpellEngine` |
| `NPCAI` | `NpcAI` |
| `MONSTERFEAR` | `NpcAI.Perception` |
| `HELPINGCRIMINALSISACRIME` | `Character.HelpingCriminalsIsACrimeEnabled` |

Bunlardan yedisi (`EQUIPPEDCAST`, `COMBATPARRYINGERA`, iki `REAGENTLOSS*`, üç
`MANALOSS*`) bu port turunun kendi dalgalarında uygulandı; ini o sırada
güncellenmedi. Sapmanın nasıl oluştuğu, neden bir koruma testi gerektiğinin de
cevabı.

`CHATFLAGS` ve `GENERICSOUNDS` aynı listede görünüyordu ama motor davranışı
yok — tek başvuruları `SERV.CHATFLAGS` / `SERV.GENERICSOUNDS` yankısı. Onlar
`[UYGULANMADI]` değil `[OKUNUYOR]` oldu.

---

## Düzeltilen sapma 2 — `ADVANCEDLOS` iki kez tanımlıydı ve yanlış belgelenmişti

İki ayrı yerde atanıyordu: çevrilmemiş kütük blokta `AdvancedLos=0` (satır 269)
ve Türkçe blokta `ADVANCEDLOS=0` (satır 1296). Ini ayrıştırıcısı anahtar adında
büyük/küçük harf ayırmadığı için ikisinden biri sessizce kaybediyordu. Her
ikisi de `0` olduğu için bugün davranış farkı yoktu; ikisi farklılaşsaydı
hangisinin kazandığı okunan dosyadan anlaşılmıyordu.

Belgelenmesi de yanlıştı. Türkçe blok değeri bir kalite kademesi gibi
anlatıyordu ("0 = klasik, 1 = standart LOS, 2 = gelişmiş LOS"). Referansta
**bit maskesi**:

```
// Source-X CServerConfig.h:474-476
#define ADVANCEDLOS_DISABLED  0x00
#define ADVANCEDLOS_PLAYER    0x01
#define ADVANCEDLOS_NPC       0x02
```

`CCharLOS.cpp:17` maskeyi `&` ile sorgular; `0x03` hem oyuncu hem NPC demektir.
Kademeli okumayla `2` "en iyi LOS" sanılırdı, oysa **sadece NPC** anlamına
geliyor — oyuncular eski yönteme düşerdi.

Yinelenen atama kaldırıldı, belge referansa göre yeniden yazıldı. Anahtar
`SphereConfig.AdvancedLos`'a okunuyor ama tüketicisi yok, o yüzden `[OKUNUYOR]`.

---

## Tüketicisi olmayan 10 anahtar

Okunur, doğrulanır, saklanır — sonra kimse sormaz. Proje kuralı bunun tersini
söylüyor: *tüketicisi olmayan ayar eklenmez*. Bunlar kural konmadan önce
birikmiş; kaydı burada, kapatılması sonraki dalgalara ait.

| Anahtar | Property | Not |
|---|---|---|
| `ADVANCEDLOS` | `AdvancedLos` | `TerrainEngine.CanSeeLOS` var ama çağrılmıyor |
| `CONNECTINGMAX` | `ConnectingMax` | Bağlanma aşamasındaki soket tavanı |
| `COLORINVISITEM` | `ColorInvisItem` | Çevrilmemiş kütük bloktan |
| `COLORINVIS` | `ColorInvis` | Çevrilmemiş kütük bloktan |
| `COLORINVISSPELL` | `ColorInvisSpell` | Çevrilmemiş kütük bloktan |
| `COLORHIDDEN` | `ColorHidden` | Çevrilmemiş kütük bloktan |
| `PETSINHERITNOTORIETY` | `PetsInheritNotoriety` | Pet noto devralma maskesi |
| `SECTORSLEEP` | `SectorSleep` | Sektör uyutma eşiği |
| `NOTOTIMEOUT` | `NotoTimeout` | Noto önbellek ömrü |
| `NETWORKTHREADS` | `NetworkThreads` | Ağ iş parçacığı sayısı |

`SAVESECTORSPERTICK` de tüketilmiyor ama **bilinçli**: `[UYUMLULUK NO-OP]` diye
işaretli, çünkü arka plan kaydı snapshot+worker modeliyle yapılıyor ve
Source-X'in tick-başına-sektör kademelemesi bu modelde karşılıksız. Kayıtlı bir
sapma, unutulmuş bir ayar değil.

---

## Bilinçli desteklenmeyen 16 anahtar

Hiçbir yerde okunmuyorlar ve `[UYGULANMADI]` diye işaretliler — yani ini
operatöre doğruyu söylüyor.

| Küme | Anahtarlar | Neden |
|---|---|---|
| Ağ ayarı | `USEASYNCNETWORK`, `NETWORKTHREADPRIORITY`, `MAXSIZECLIENTIN`, `MAXSIZECLIENTOUT`, `MAXSIZEPERTICK`, `MAXQUEUESIZE`, `USEEXTRABUFFER`, `USEPACKETPRIORITY` | Source-X'in kendi soket yığınının ayarları; SphereNet'in ağ katmanı farklı |
| Bağlantı | `CONNECTINGMAXIP` | IP başına bağlanma tavanı |
| Dünya | `MAPCACHETIME`, `MONSTERTIGHT`, `DISTANCEFORMULA` | Referansın önbellek/yerleşim/mesafe iç detayları |
| Karakter | `STATSFLAGS`, `HITSUPDATERATE`, `CANUNDRESSPETS` | — |
| Büyü | `SPELLTIMEOUT` | — |

`APPUPDATEREPODIR` işaretsiz ve okunmuyor; diğer `APPUPDATE*` anahtarları
`SphereNet.Host` tarafından okunuyor.

---

## Koruma testi

`IniKeyClassificationGuardrailTests` üç şeyi sabitler:

1. `config/sphere.ini` aynı anahtarı iki kez tanımlamaz (büyük/küçük harf
   ayrımsız).
2. `[UYGULANMADI]` işaretli bir anahtar kaynakta düz metin olarak geçmez —
   yani bu belgedeki 1. sapma tekrar oluşamaz. Bir dalga ayarı uygularsa test
   ini güncellenene kadar kırmızı kalır.
3. Bu belgedeki sayılar ini'nin işaret sayımıyla uyuşur.

Testin göremediği tek şey, ini'de hiç işaret taşımayan 31 anahtar. Onların
çoğu tüketiliyor; işaretlenmeleri kozmetik bir borç, davranış değil.
