# Gorsel Senkron Denetimi - animasyon, yon, delta gorunum, ekran yenilenme

Bu belge, istemcinin ekranindaki bir seyin sunucudaki gercekle ayrisabilecegi
**her yolu** tek tek gezip bulunan eksikleri kaydeder. Kapsam: mobil/esya cizim
paketleri, delta gorunumun degisiklik tespiti, animasyon gonderimi, karakterin
yonu ve gorsel durum bildirimleri.

Yontem: her bulgu icin (a) belirti, (b) koddaki kok, (c) Source-X karsiligi,
(d) onerilen duzeltme. Referanslar `oldSphere/Source-X-full/src` ve
`oldSphere/ClassicUO-main/src` agaclarina, satir numarasiyla.

**Durum etiketleri**

- `UYGULANDI` - duzeltildi ve testi yazildi.
- `DOGRULANDI` - kod okunarak teyit edildi, davranis farki kesin.
- `SUPHELI` - kod farki teyit edildi ama sahadaki etkisi olculmedi.
- `ARASTIRILACAK` - isaret var, dogrulama icin ek is gerekiyor.

---

## 0. Model: istemci neyi nereden ogrenir

Bir mobili ekranda dogru cizmek icin istemcinin bildigi her sey su dort
kaynaktan gelir:

| Ne | Paket | Bizde tetikleyen |
|---|---|---|
| Konum / yon / govde / renk | 0x77, 0x78, 0x20 | delta gorunum demeti |
| Durum bayraklari (savas, donmus, zehirli, sari can, gizli) | ayni paketlerin flags bayti | `BuildMobileFlags` |
| Notorluk rengi | ayni paketlerin noto bayti | `ComputeNotoriety` (izleyici-bazli) |
| Kusanilan esya | 0x78 ekipman listesi, 0x2E | equip/unequip cagri yerleri |
| Isim / ozellik balonu | 0xD6 OPL, 0xDC revizyon | kirli-nesne tooltip yolu |

Delta gorunum bir karakteri su **yedili demetle** takip eder:

    (X, Y, Z, Dir, BodyId, Hue, VisKey)

`VisKey` alti bit tasir: Hidden, Invisible, Dead, WarMode, Criminal, Murderer
(`ClientViewUpdater.ComputeVisKey`, 369-385).

**Bu demetin disinda kalan her gorsel durum, delta gorunum tarafindan
farkedilmez.** Asagidaki bulgularin cogu bu tek cumlenin sonucu.

---

## 1. `UYGULANDI` - Mobil bayrak bayti eksik ve izleyiciden bagimsiz

**Belirti:** Yenilmez (invul) bir yaratigin can barasi sari gorunmuyor.
Zehirlenmis bir karakter eski istemcilerde yesil gorunmuyor. Tasa cevrilmis
(STONE) bir karakter donmus gorunmuyor. Elf/gargoyle disi karakterler erkek
olarak ciziliyor. Uyuyan / insubstantial karakterler gri cizilmiyor.

**Kok:** `GameClient.BuildMobileFlags` (GameClient.ScriptConsole.cs:153-162)
bes bit kuruyor ve izleyicinin istemci surumunu hic dikkate almiyor.

Source-X `CChar::GetModeFlag(pViewer)` (CCharStatus.cpp:659-702) ile karsilastirma:

| Bit | Source-X | Bizde |
|---|---|---|
| 0x01 FREEZE | `STATF_FREEZE` veya `STATF_STONE` | yalnizca Freeze - **STONE yok** |
| 0x02 FEMALE | `pCharDef->IsFemale()` (tanimin cinsiyeti) | `BodyId == 0x0191 \|\| 0x0193` - **yalnizca insan** |
| 0x04 FLYING / POISON | **izleyici SA+ ise** HOVERING, **degilse** POISONED | her zaman HOVERING - **zehir hic gonderilmiyor, izleyiciye bagli degil** |
| 0x08 YELLOW | `STATF_INVUL` | **yok** |
| 0x10 IGNOREMOBS | `GetPrivLevel() > PLEVEL_Player` | **yok** |
| 0x40 WAR | `STATF_WAR` | var |
| 0x80 INVIS | Sleeping + (ini renk anahtarlarina gore) Insubstantial / Hidden / Invisible | yalnizca `IsInvisible` - **Sleeping, Insubstantial, Hidden yok** |

Istemci tarafi teyit: `ClassicUO/Game/Data/EntityFlags.cs:9-19` ve
`Game/GameObjects/Mobile.cs:123-145` - `IsPoisoned` 7.0 altinda 0x04 bitini,
`IsFlying` 7.0 ustunde **ayni biti** okuyor. Yani 0x04'un anlami izleyicinin
surumune gore degisiyor, sabit degil.

**Uygulandi:** `BuildMobileFlagsFor(Character, NetState?)` ve her istemcinin
kendi `BuildMobileFlags(Character)` ornek metodu. Eksik yedi durum eklendi,
0x04 izleyicinin surumune gore seciliyor, COLORINVIS/COLORHIDDEN/COLORINVISSPELL
ini anahtarlari `GameClient.ColorInvisHue` ve kardeslerine baglandi.
Testler: `MobileFlagsTests`.

**Risk:** Dusuk. Bit basina ayri test yazilabilir.

---

## 2. `UYGULANDI` - VisKey bayrak degisimlerini gormuyor

**Belirti:** Yukaridaki bayraklar duzeltilse bile, bir karakter **yerinde
dururken** donarsa / yenilmez olursa / zehirlenirse / ucmaya baslarsa ekranda
hicbir sey degismiyor; ancak karakter hareket edince duzeliyor.

**Kok:** `ComputeVisKey` alti bit tasiyor (Hidden, Invisible, Dead, WarMode,
Criminal, Murderer). Freeze/Stone, Invul, Hovering, Sleeping, PrivLevel
degisimleri demette yok, dolayisiyla `visChanged` asla dogru olmuyor.

Bu, gecmis dalgalarda kriminal/katil bitlerinin **ayni sebeple** eklenmis
olmasiyla ayni sinif - o zamanki belirti "duran saldirgan mavi kaliyordu" idi
(ClientViewUpdater.cs:376-383 yorumu).

**Oneri:** `VisKey`'i byte'tan ushort'a cikarip bayrak baytini **oldugu gibi**
anahtarin icine koymak en saglami: boylece bayrak seti her
genisledikce anahtar kendiliginden dogru olur. Iki bit bos oldugundan tek tek bit eklemek de
mumkun ama ayni hatayi tekrar etmeye acik.

**Risk:** Dusuk. Anahtar yalnizca karsilastirmada kullaniliyor.

---

## 3. `UYGULANDI` - Notorluk izleyici-bazli, degisiklik tespiti karakter-bazli

**Belirti:** Lonca savasi ilan edilince, gruba katilinca veya biri size
saldirinca karsi tarafin **isim ve can barasi rengi** guncellenmiyor; o karakter
hareket edene kadar eski renkte kaliyor.

**Kok:** `GetNotoriety` izleyiciyi hesaba katiyor
(`ComputeNotoriety(_world, _character, ch)`, GameClient.ScriptConsole.cs:719) -
bu dogru. Ama degisiklik tespiti `VisKey` ile yapiliyor ve o **karakterin kendi
durumundan** turetiliyor. Iliski degisince hedefin kendi durumu degismedigi icin
hicbir izleyicide yeniden gonderim tetiklenmiyor.

**Uygulandi:** `LastKnownPos` demetine `Noto` alani eklendi ve karsilastirmaya
girdi. Demet zaten istemci basina tutuldugu icin izleyici-bazli deger dogal
olarak yerine oturdu.

**Risk:** Dusuk-orta. Demete alan eklemek her tick bir bayt daha karsilastirir.

---

## 4. `UYGULANDI` - Animasyon cagri yerlerinin cogu ham 0x6E gonderiyor

**Belirti:** Ata binmis bir karakter yemek yiyince, egilince veya zanaat
yapinca istemci **yaya** animasyonunu oynatiyor; ClassicUO bunu cizerken binici
gorsel olarak attan inip biniyor. KR/Enhanced istemciler bu animasyonlari hic
gormuyor.

**Kok:** Motorda iki ayri dogru yol var ama ikisi de genel degil.

- `GameClient.BroadcastAnimation` (GameClient.PacketHelpers.cs:366-388) izleyici
  basina KR/Enhanced icin 0xE2, digerleri icin 0x6E seciyor. **Surum secimini
  yapan tek yer burasi.**
- `ActiveSkillEngine.BroadcastAnimation` (1287-1304) govde ve binicilik cevrimini
  uyguluyor ama yalnizca 0x6E gonderiyor.

Animasyon kuran 18 satir sayildi. Bunlardan **11'i ne govde/binicilik cevrimi
ne de surum secimi yapiyor**:

    ClientItemUseHandler.cs:849, 1986, 2177, 2842, 3002
    ClientSkillsHandler.cs:344
    ClientWorldFeaturesHandler.cs:429, 2225
    GameClient.PacketHelpers.cs:333  (PlayOwnAnimation - .ANIM komutu)
    GameClient.Skills.cs:76
    SkillHandlers.cs:547             (BroadcastSkillAnimation)

Uc satir kismen dogru:

    SpellEngine.cs:3745 (sarhos *hic*) - govde cevrimi yok; atli durum bir
                                         guard ile zaten disarida
    ActiveSkillEngine.cs:1325        - her iki cevrim de var, surum secimi yok
    Program.EngineWiring.cs (darbe alma, sifaci) - her iki cevrim var, surum
                                         secimi yok

Character.cs:5710-5740 (ANIM, BOW, SALUTE verb'leri) her iki cevrimi de dogru
yapiyor; eksikleri yalnizca surum secimi.

`BodyAnimTranslator.ToMounted` (BodyAnimTranslator.cs:25-43) `Bow`, `Salute` ve
`Eat` icin acikca `HorseSlap`'e cevirir - yani atlanan cevrim tam olarak bu
cagri yerlerinin ihtiyaci olan sey.

**Uygulandi:** `GameClient.PlayAnimation` - cevrim (govde + binicilik) ve
izleyici basina paket secimi tek kapida. 11 ham cagri yeri oraya baglandi;
SpellEngine'in sarhos animasyonu ve sunucu tarafindaki darbe-alma/sifaci
animasyonlari da ayni kapidan geciyor. `AnimationDoorTests` icinde bir guardrail
testi, kapinin disinda kalan her dosyayi isimle raporlar.

**Kalan:** `Character.cs` ve `ActiveSkillEngine.cs` cevrimleri kendileri yapiyor
ama izleyici listesine erisemedikleri icin 0xE2 secimini yapamiyor - motor
tarafinda `ForEachClientInRange` esdegeri bir statik yok. KR/Enhanced istemciler
bu iki yolun animasyonlarini hala gormuyor.

**Risk:** Dusuk. Davranis yalnizca dogru yone degisir.

---

## 5. `UYGULANDI` - Kirli nesne bildirimi giyili esyada yanlis konumu kullaniyor

**Belirti:** Giyili bir esyanin ozelligi degisince (isim, renk, tur) yakindaki
istemciler yenilenmek uzere isaretlenmiyor ve ozellik balonu kimseye gitmiyor.

**Kok:** `Program.NetworkHandlers.MarkClientsNearDirtyObject` (742-780)
`obj.Position` kullaniyor. Giyili bir esyanin kendi konumu **en son yerde
yattigi yer** (ya da 0,0) - yani yanlis sektorun istemcileri isaretleniyor,
cogu zaman hic kimse.

Bu, bu oturumda **silme** yolunda duzeltilen hatanin ayni sinifi: orada
`GetTopLevelObj().Position` kullanmaya gecildi, burada hala eski hali duruyor.

**Uygulandi:** `obj.GetTopLevelObj().Position`.

**Risk:** Cok dusuk. Tek satir, desen zaten uygulandi.

---

## 6. `UYGULANDI` - Giyili esyanin renk/grafik degisimi hicbir demette yok

**Belirti:** Script bir cubbenin `COLOR` veya `ID` degerini degistirince giyen
kisi de izleyiciler de degisikligi resync'e kadar gormuyor.

**Kok:** Delta gorunum iki demet tutuyor: karakter demeti (govde ve karakterin
kendi rengi) ve **yer** esyasi demeti. Giyili bir esya ikisinde de yok.
`Item.OnVisualUpdate` kancasi bu is icin var ama yalnizca elle secilmis bes
yerden cagriliyor (bandaj, yatak rulosu, meyve, balik, fici) - genel bir
`Hue` / `BaseId` degisimi onu tetiklemiyor (`ObjBase.Hue` setter'i yalnizca
`MarkDirty(DirtyFlag.Hue)` yapar).

**Uygulandi:** Kirli-nesne pasinda giyili bir esyanin Hue/Body kirliligi
gorulunce ayni dongudeki her izleyiciye `SendItemVisualUpdate` (0x2E) gidiyor.
Testler: `WornItemVisualTests`.

**Risk:** Dusuk-orta. Cok sik degisen bir esya her tick bir 0x2E uretebilir;
kirli-set zaten tick basina tekillestiriyor.

---

## 7. `UYGULANDI` - `Item.OnVisualUpdate` menzilsiz yayin yapiyor

**Kok:** `Program.EngineWiring.cs:972-976` **tum** istemcilere gonderiyor:

    Item.OnVisualUpdate = item => { foreach (var c in _clients.Values) c.SendItemVisualUpdate(item); };

Kalabalik bir shard'da her gorsel guncelleme oyuncu sayisi kadar paket uretir.

**Uygulandi:** `ForEachClientInRange(item.GetTopLevelObj().Position, 18, ...)`.

**Risk:** Cok dusuk.

---

## 8. `UYGULANDI` - Hedefe donme birkac beceride eksik

**Belirti:** Evcillestirme sirasinda karakter hayvana donmuyor; animasyon yanlis
eksende oynuyor.

**Kok:** Source-X `Skill_Taming` icinde `UpdateDir(pChar)` var
(CCharSkill.cpp:2307). Bizim `ActiveSkillEngine.Taming` (668 ve devami) hicbir
donus yapmiyor. Bu oturumda madencilik/balikcilik/oduncululuk icin eklenen
`FaceSkillTarget` yardimcisi hazir duruyor.

Upstream'de `UpdateDir` cagrilan diger yerler - her biri ayrica kontrol
edilmeli: CCharSkill.cpp:1127 (eritme), 2252 (kamp atesi), 3155 (ocak), 3276,
3368, 3608 (vurus dongusu - bu oturumda eklendi).

**Uygulandi:** Evcillestirme artik hayvana donuyor. Upstream'in diger UpdateDir
noktalari (eritme, kamp atesi, ocak) hala gozden gecirilmedi.

**Risk:** Cok dusuk.

---

## 9. `SUPHELI` - Yer esyasi bayrak bayti yalnizca GM'e gonderiliyor

**Kok:** `GameClient.BuildWorldItemPacket` (GameClient.PacketHelpers.cs:697-714)
flags baytina yalnizca GM icin 0x20 (movable) koyuyor. Normal oyuncuda bayrak
bayti hic gonderilmiyor.

ClassicUO `Flags.Movable = 0x20` bitini taniyor; tasinabilir bir esyanin
suruklenmeye baslamasi istemci tarafinda bu bite bagli olabilir. Koddaki yorum
kisitin yalnizca "tiledata agirligi 255 olan grafikler" icin gecerli oldugunu
soyluyor - yani siradan esyalar icin sorun olmayabilir.

**Yapilacak:** Source-X'in ayni bayti nasil doldurdugunu (`PacketItemWorld`)
okuyup karsilastirmak. Bu denetimde yapilmadi.

---

## 10. `ARASTIRILACAK` - Animasyon kare/tekrar varsayilanlari

`PacketAnimation` varsayilanlari `frameCount: 7, repeatCount: 1, forward: true,
repeat: false, delay: 0` (ExtendedPackets.cs:1404). Source-X'in ayni pakette
eylem basina farkli kare sayilari kullanip kullanmadigi karsilastirilmadi.
Sahadan gelen "madencilik hareketi daha kisa gibi" gozlemi buraya da
bakilmasini gerektirebilir.

---

## 11. `ARASTIRILACAK` - Coklu yapilarin (multi/house) gorsel yenilenmesi

Delta gorunum coklu yapilari yer esyasi gibi takip ediyor (`IsMultiBody` dali).
Bir evin tasarimi degistiginde (0xD8 ozel ev akisi) veya bir gemi dondugunde
bilesenlerin yeniden cizimi ayri yollardan gidiyor. Bu denetimde yalnizca gemi
yolcusu yolu incelendi (ve duzeltildi); ev tasarim akisi acik.

---

## Bu oturumda zaten kapatilanlar

Asagidakiler ayni sinifin daha once bulunmus ornekleri; belge butunlugu icin
listeleniyor (hepsi commit'li):

- Redeed edilen deed sahibinin cantasina konup kimseye soylenmiyordu.
- Giyili/kap icindeki bir esyanin **silinmesi** hicbir ekranda degismiyordu.
- Durum penceresi (0x11) yalnizca elle isteyen cagri yerlerinden tazeleniyordu.
- Gemi yolcusu her adimda 0x77 aliyor, yerinde yuruyor gorunuyordu.
- Dirilen karakter olum oncesi bedenine degil duz insana donuyordu.
- Olum/dirilme yuruyus sirasini sifirlamiyordu; hayalet cesetten uzakta kaliyordu.
- Mevsim paketi degismemis mevsim icin de gonderiliyordu (istemci her yuklu
  parcanin grafigini yeniden turetiyor).
- Toplama becerileri hedefe donmuyordu; madencilik/balikcilik sesleri yanlisti.
- Yere birakilan esya yanlis grafige flip oluyordu (DUPELIST dongusu).

---

## Durum

Bulgu 1-8 uygulandi ve testleri yazildi (`MobileFlagsTests`, `AnimationDoorTests`,
`WornItemVisualTests`; VisKey ve notorluk mevcut delta testlerinin kapsaminda).
Bulgu 9, 10 ve 11 arastirma bekliyor.

Ayrica acik kalan iki nokta:

- KR/Enhanced istemciler `Character.cs` ve `ActiveSkillEngine.cs` uzerinden giden
  animasyonlari hala gormuyor: motor tarafinda izleyici listesine erisecek bir
  statik yok (bkz. Bulgu 4, "Kalan").
- Upstream'in `UpdateDir` noktalarindan eritme (CCharSkill.cpp:1127), kamp atesi
  (:2252) ve ocak (:3155) gozden gecirilmedi (bkz. Bulgu 8).

---

## Onerilen sira

1. **Bulgu 5** - tek satir, desen zaten uygulandi.
2. **Bulgu 1 + 2** birlikte - bayrak bayti ve anahtar ayni isin iki yuzu.
3. **Bulgu 4** - animasyon yardimcisi + guardrail testi.
4. **Bulgu 3** - notorlugu demete al.
5. **Bulgu 6 + 7** - giyili esya gorsel guncellemesi.
6. **Bulgu 8** - kalan becerilerde donus.
7. Bulgu 9, 10, 11 icin once arastirma.

Her madde icin bu oturumda kullanilan yontem gecerli: once davranisi sabitleyen
bir test yaz, duzeltmeden once kirmizi oldugunu gor, sonra duzelt.
