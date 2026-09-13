# SphereNet ↔ Source-X Port Durum Raporu

**Tarih:** 2026-09-13 (ilk sürüm 2026-09-06; sayılar PLAN-004'te yeniden ölçüldü)
**Ölçüm dayanağı:** `oldSphere/Source-X-full/src` (referans C++ motor, 201.846 satır)
karşısında `src/` (SphereNet, test hariç 153.261 satır / 337 dosya).
**Doğrulama:** `dotnet test src/SphereNet.Tests/SphereNet.Tests.csproj` →
**3646 test, 0 başarısız, 0 atlanan**; koşunun 66 veri kapısından biri veri
bulamıyor ([veri kapıları](VERI_KAPILARI_TR.md)).
**Paydalar:** [Source-X tablo paydaları](SOURCEX_TABLO_PAYDALARI_TR.md) ve
`docs/data/sourcex_tables.csv` (98 tablo, 3570 giriş) — her payda oradan türetilir.

---

## 0. Ölçüm yöntemi ve güven sınırları

Bu bölümü atlamayın: **rapordaki iki sütun aynı cinsten değil.**

**Payda sayılır.** Source-X script'e açtığı her yüzeyi `src/tables/*.tbl` ve
`.cpp` içi tablolarda listeler. Bu tabloların tamamı
`docs/data/sourcex_tables.csv`'ye çıkarıldı (98 tablo, 3570 giriş) ve
`SourceXTableInventoryGuardrailTests` her koşuda referanstan yeniden üretip
karşılaştırıyor. Buradaki her payda o dosyadan gelir, elle yazılmaz.

**Pay tahmin edilir.** "Bu ismi karşılıyor muyuz" sorusu kaynak taramasıyla
cevaplanıyor ve tarama üç ayrı gönderim biçimini görmek zorunda:

| Biçim | Örnek | Çıplak literal taraması |
|---|---|---|
| Düz literal | `case "DCLICK":` | görür |
| Son ekli literal | `StartsWith("FEVAL (")`, `"CANMAKE."` | **görmez** |
| Tanımlayıcı / enum | `ScriptKey.Asc => …` | **görmez** |

`CScriptObj_functions`'ın 54 adı bu üç kovaya şöyle dağılıyor: 11 düz literal,
4 son ekli, 30 tanımlayıcı, 9 yok. Yalnız düz literale bakan bir tarama bu
tabloda **%20 kapsam** bulur; üçüne birden bakan **%83**. Aradaki fark yöntem
farkıdır, kod farkı değil.

Bu yüzden paylar **±%5 bandıyla** okunmalı ve iki anlamlı basamakla sunulmamalı.
Tek başına bir ismin kaynakta bulunması, davranışın birebir portlandığını da
kanıtlamaz.

**Sadakat sütunu hiç ölçülmedi.** O bir **değerlendirme**: kod okuma, guardrail
testleri, `docs/reviews/` ve uyduruk-değer denetim geçmişiyle verilmiş bir
kanaat. 100 üzerinden yazılması onu ölçüm yapmaz; karşılaştırma kolaylığı için
sayıya çevrilmiş bir yargıdır. Aynı şey ağırlıklı özetteki tek sayılar için de
geçerli.

**Alias sapması:** Source-X `SKILL_START`/`@Start` gibi bağlam-bağımlı isimleri
SphereNet `SkillStart` diye adlandırır; ham diff bunları "eksik" sayar. Trigger
ve spell listelerinde alias eşlemesi elle yapıldı.

---

## 1. Genel tablo

**Kapsam** sayımdan türetilir (±%5, bkz. §0). **Sadakat** sayılmadı; bir
değerlendirmedir. İkisini toplamayın.

| # | Kategori | Kapsam ~ | Sadakat (kanaat) | Kanıt |
|---|---|---:|---:|---|
| 1 | Trigger sistemi (`@Trigger`, EVENTS/TEVENTS zinciri) | **89** | **90** | `triggers.tbl` 248 / sınıf tabloları 252 / birleşim 253; ~221 karşılığı var; ateşlenmeyen backlog = 1 (`@UserVirtue`) |
| 2 | Script motoru / ifade motoru | **85** | **88** | `CScriptObj_functions` 44/54; FEVAL/FLOATVAL/STRSUB/LOCAL/REF ailesi test kilitli |
| 3 | Nesne fiilleri (verbs) | **100** | **88** | `CObjBase/CChar/CItem/CClient_functions` 206 tablo girişi = 186 ayrı anahtar, tamamı yollu |
| 4 | Nesne özellikleri (props) | **73** | **85** | §2.2'deki on bir nesne-özellik tablosu 390/534 (bileşen props ayrı, bkz. 26) |
| 5 | Ağ / paket katmanı | **85** | **88** | 65 kayıtlı gelen handler + alt-dispatch; 109 giden paket sınıfı (SX 124) |
| 6 | Şifreleme / login zinciri | **95** | **92** | Blowfish + Twofish + Huffman + no-crypt, loopback login entegrasyon testi |
| 7 | Karakter / Item çekirdek modeli | **95** | **88** | `IT_*` 207/212, `SKILL_*` 60/60, `SPELL_*` 211/211 (gerçek büyü) |
| 8 | Savaş (combat) | **88** | **86** | Swing state machine, `@Hit*` ailesi, archery/parry/noto; C1–C7 parite dalgaları |
| 9 | Büyü (magic) | **82** | **88** | Canlı pakette 168 büyü tanımlı, 138'i çalışıyor, 30'u reddediliyor — okul bazında döküm: [BÜYÜ MATRİSİ](BUYU_MATRISI_TR.md) |
| 10 | Skill sistemi | **85** | **90** | 60 skill tanımlı, 56'sı motorda referanslı; `Skill_Experience` birebir portlanmış |
| 11 | NPC AI / pet / vendor beyni | **85** | **82** | 4.913 satır (SX ~4.500); beyin tik temposu bilinçli sapma |
| 12 | Ölüm / ceset / yağma | **90** | **88** | `@Death`/`@Kill`/`@DeathCorpse`, NOCORPSE, 2 aşamalı decay |
| 13 | Vendor / ticaret / stable | **88** | **88** | `@Buy`/`@Sell` RETURN 1 vetosu, ham `0x3B`/`0x9F` roundtrip testi |
| 14 | Crafting / gathering | **85** | **85** | `Skill_MakeItem` band tablosu portlandı, stroke=DELAY×sayı modeli |
| 15 | Bölge / sektör / dünya | **88** | **88** | Region anahtarları 35/40, Sector 22/24 |
| 16 | Kalıcılık (save/load) | **88** | **90** | Klasik 56T save yükleniyor (2.660 NPC + 53 spawner); native `SAVESTATICS` yok |
| 17 | Hesap sistemi | **90** | **88** | `CAccount` anahtarları 44/46 |
| 18 | Gemi (ship) / multi hareketi | **85** | **85** | Diagonal sail, IsOnDeck, `@Ship_*` trigger'ları |
| 19 | Party | **88** | **85** | `CParty_functions` 9/10, `CParty_props` 6/7 |
| 20 | Gump / dialog / hedefleme | **88** | **88** | `CClient_functions` 61/61; dialog layout verb kapsaması script setine göre tam |
| 21 | Konuşma / speech / keyword | **85** | **85** | Command prefix güvenlik kapısı, HasWord kelime eşleşmesi |
| 22 | Harita verisi (`.mul`) | **80** | **85** | map/statics/multi/tiledata okuyucular var; map diff (`USEMAPDIFFS`) yok |
| 23 | Housing / multi script API | **~80** | **80** | `CItemMulti` anahtarları 58/70; addon/key/component-silme ailesi kaldı |
| 24 | Chat (conference / global) | **70** | **75** | 304 satır (SX ~1.300); `0xB3`/`0xB5` çalışıyor, `0xB2` legacy + `0xF9` ertelendi |
| 25 | Guild stone menü sistemi | **~63** | **70** | `CItemStone_functions` 19/30; gump tabanlı stone menüleri yok |
| 26 | AOS bileşen-prop sistemi (`CCProps*`) | **55** | **80** | 77/139; direnç/regen/slayer/hit-* çekirdeği var, SA/ML/TOL uzun kuyruğu yok |
| 27 | `sphere.ini` konfigürasyon yüzeyi | **58** | **85** | 162/279 anahtar; **en zayıf ölçülen alan** |
| 28 | Sunucu / admin verb'leri (`SERV.*`) | **90** | **85** | 33 `sm_szVerbKeys` girişinin tamamı yollu; `SAVESTATICS` + güvenlik-hassas işler açık |

### Ağırlıklı özet

| Eksen | Değer | Cinsi |
|---|---:|---|
| **Kapsam** — Source-X yüzeyinin ne kadarı port edildi | **~82** | sayımdan, ±%5 |
| **Sadakat** — port edilen kısım ne kadar doğru | **86** | kanaat, ölçülmedi |
| **Sphere 56x hedefine göre kapsam** (AOS/SA/ML uzun kuyruğu hariç) | **~90** | sayımdan, ±%5 |

Ağırlıklandırma, kategorinin bir shard'ın ayakta durması için gerekliliğine
göre yapıldı: trigger/script/paket/persistence ×3, combat/magic/skill/AI ×2,
housing/chat/guild/AOS-props ×1.

---

## 2. Kategori detayları — nerede ne eksik

### 2.1 Trigger sistemi — 89 / 90

Trigger paydası tek sayı değil: `triggers.tbl` (sıralı global liste) **248**,
sınıf tablolarının birleşimi **252**, ikisinin birleşimi **253**. Listeler
birbirini kapsamıyor — `ITEMFIRE` yalnızca global listede, beş bağlam-menüsü ve
bölge trigger'ı yalnızca sınıf tablolarında
([paydalar](SOURCEX_TABLO_PAYDALARI_TR.md)).

SphereNet `CharTrigger` + `ItemTrigger` enum'ları 218 üye tanımlıyor; alias
eşlemesinden sonra **~221 Source-X trigger'ının karşılığı var**.

`TriggerCoverageGuardrailTests` her koşuda "tanımlı ama hiç ateşlenmiyor"
kümesini kaynaktan yeniden hesaplayıp dokümante backlog'a karşı doğruluyor.
Bugün o backlog **tek üye**: `@UserVirtue` (virtue gump'ı yok). Item trigger
backlog'u **boş**.

**Gerçekten eksik olanlar (~27):**

| Trigger | Alan |
|---|---|
| `@AfkMode`, `@Jailed`, `@Load`, `@SendPaperdoll` | oturum/karakter yaşam döngüsü |
| `@CharShove`, `@Falling`, `@ToggleFlying`, `@SeeHidden` | hareket / algı |
| `@PayGold`, `@PetRelease`, `@FollowersUpdate` | ekonomi / pet |
| `@RegenStat`, `@HitReactive` | stat / savaş kenar yolları |
| `@RegionResourceFound`, `@RegionResourceGather`, `@ResourceFound` | kaynak toplama |
| `@ArrowQuest_Add`, `@ArrowQuest_Close` | quest oku |
| `@HouseDesignCommitItem`, `@DelMulti` | custom housing |
| `@ClientTooltip_AfterDefault` ailesi (3 varyant) | AOS tooltip |
| `@itemFire`, `@itemSmelt`, `@itemSpell`, `@itemCarveCorpse` | char üzerindeki item ayna trigger'ları |

Sadakat tarafı güçlü: zincir sırası (`@Char*` → EVENTS → TEVENTS → CHARDEF →
EVENTSPET/PLAYER), `ARGN1/2/3` geri-yazımı, `ARGS`/`ARGO`/`LOCAL` paylaşımı,
`RETURN 1` iptali ve Source-X'in `IsTrigUsed` sıcak-yol kapısı portlanmış.

### 2.2 Script yüzeyi — fiiller 100, özellikler 73

Aşağıdaki on sekiz tablo Source-X'in script'e açtığı ana prop + fonksiyon
yüzeyini taşıyor: **837 giriş**, SphereNet'te **~670'i** çözülüyor (~%80).
Paylar §0'daki üç gönderim biçimine birden bakan taramayla, **±%5 bandıyla**
okunmalı.

Ayrıştırıldığında tablo net bir şekil alıyor:

| Tablo | Kapsam |
|---|---:|
| `CObjBase_functions` (fiiller) | 57/57 — **%100** |
| `CChar_functions` | 74/74 — **%100** |
| `CItem_functions` | 14/14 — **%100** |
| `CClient_functions` | 61/61 — **%100** |
| `CCharBase_props` | 40/40 — **%100** |
| `CItemStone_props` | 18/18 — **%100** |
| `CSector_functions` | 12/13 — %92 |
| `CStoneMember_props` | 15/15 — **%100** |
| `CChar_props` | 109/124 — %88 |
| `CScriptObj_functions` | 43/54 — %80 |
| `CCharPlayer_props` | 25/31 — %80 |
| `CBaseBaseDef_props` | 20/25 — %80 |
| `CCharNpc_props` | 10/14 — %71 |
| `CClient_props` | 14/20 — %70 |
| `CObjBase_props` | 46/73 — %63 |
| `CItem_props` | 51/91 — %56 |
| `CItemBase_props` | 42/83 — %51 |
| `CItemStone_functions` | 19/30 — %63 |

**Okuma:** *fiil* tarafı tamamlanmış, *özellik* tarafında delik var — ve deliğin
büyük kısmı tek bir yerden geliyor: **AOS/SE/ML çağı item özellik sistemi.**
`CItem_props` + `CItemBase_props` eksiklerinin ~35'i `BONUSSKILL1..5`,
`ITEMSET*`, `NPCKILLER`, `NPCPROTECTION`, `RECHARGE*`, `SELFREPAIR`,
`SUMMONING`, `RARITY`, `IMBUE`, `REFORGE`, `ENCHANT`, `RECIPE*` ailesi.
Bir Sphere 56x shard'ı bunları kullanmaz.

**Sphere 56x için gerçekten canını yakacak eksikler.** İlk sürümde 43 isim
sayılmıştı; 16'sı o tarihten sonra cevaplandı (`CANCAST`, `CANMAKE`,
`CANMAKESKILL`, `SKILLUSEQUICK`, `SKILLBEST`, `SWING`, `BREATH`, `MEMORY`,
`DROPSOUND`, `EQUIPSOUND`, `RESDEF`, `TAGAT`, `ISEVENT`, `ISTEVENT`,
`ISDIALOGOPEN`, `TOPCONT`). Kalan **27**:

| İsim | Neden önemli |
|---|---|
| `MODMAXHITS` / `MODMAXMANA` / `MODMAXSTAM` | script'te sık kullanılan stat tavanı değiştiricileri |
| `SPELLTIMEOUT` | büyü kapısı |
| `SKILLCHECK`, `SKILLTEST`, `SKILLADJUSTED` | skill sorgulama ailesi |
| `FIGHTRANGE`, `DAMADJUSTED` | savaş sorguları |
| `PICKUPSOUND` / `DOOROPENSOUND` / `DOORCLOSESOUND` | item ses tablosunun kalanı |
| `RESDEF0`, `STRTOKEN`, `LISTCOL`, `STRFIRSTCAP`, `STRRANDRANGE` | script yardımcı fonksiyonları |
| `PROPSAT`, `PROPSCOUNT`, `CTAGCOUNT`, `DIALOGLIST` | koleksiyon indeksleme |
| `ISCONT`, `ISNEARTYPETOP` | predicate ailesi |
| `OWNEDBY`, `NODROP`, `NOTRADE`, `QUESTITEM` | item sahiplik/kısıt bayrakları |

`SYSCMD` ve `SYSSPAWN` (script'ten OS komutu çalıştırma) **bilinçli olarak
portlanmadı** — güvenlik kararı, eksik değil.

### 2.3 Ağ katmanı — 85 / 88

- **Gelen:** `NetworkManager` 65 paket sınıfı kaydediyor. Source-X `receive.h`
  108 sınıf tanımlıyor, ama bunların 14'ü custom-house design alt-komutları;
  SphereNet onları tek `EncodedCommand` + `0xD7` alt-dispatch'iyle karşılıyor.
  Etkin kapsam ~%85.
- **Giden:** 109 paket sınıfı (SX 124). Eksikler: `BondedStatus`,
  `ChangeCharacter`, `CharacterListUpdate`, `CloseContainer`, `CloseVendor`,
  `GameTime`, `GlobalChat`, `GumpChange`, `PropertyListVersionOld`,
  `QueryClient`, `SignGump`, `StatueAnimation`, `Telnet`, `TimeSyncResponse`,
  `ToggleHotbar`, `WarningMessage`, `WebPage`, `ZoneChange`.
- **Şifreleme tam:** Blowfish + Twofish + Huffman + no-crypt algılama, gerçek
  socket olmadan `InjectReceived`/`ProcessInput` üzerinden deterministik
  loopback login testi (`0x80` → `0xA8` → relay `0x8C` → `0x91` → `0xB9`/`0xA9`
  → `0x5D` → `0x1B`).
- Client-çağı kapıları (`ClientEra=Sphere56x` varsayılan, `0xDF` buff ve AOS
  tooltip sadece destekleyen client'ta) test kilitli.

### 2.4 Housing / multi — ~80 / 80

Source-X `CItemMulti.cpp` (3.942) + `CItemMultiCustom.cpp` (2.073) = 6.015 satır.
SphereNet Housing = 1.971 satır.

**Var olan:** yerleştirme + hesap limitleri, sahip/co-owner/friend/ban listeleri,
lockdown/secure sayaçları (`GetMaxLockdowns` birebir), decay aşamaları,
`HOUSE.n` script API'si, custom housing editörü (`0xD7`/`0xD8` design stream,
revision'lı commit, `DESIGN_n` tag kalıcılığı, `WalkCheck.ResolveCustomDesign`
üzerinden sanal yürüme geometrisi), ship redeed crate.

İlk sürümde bu bölüm "ev çalışır, ev script'lenemez" diyordu ve 19 eksik ad
sayıyordu. Dokuzu o tarihten sonra geldi: `ADDCOMPONENT`, `ADDONS`,
`ADDVENDOR`, `MOVINGCRATE`, `SECURED` ve `GET*POS` indeksleme ailesi
(`GETCOMPPOS`, `GETFRIENDPOS`, `GETSECUREDCONTAINERS`, `GETLOCKEDITEMPOS`).

**Kalan 10:** `DELCOMPONENT`, `ADDADDON`/`DELADDON`, `DELVENDOR`,
`ADDKEY`/`REMOVEKEYS`, `GENERATEBASECOMPONENTS`, `MOVEALLTOCRATE`,
`MOVELOCKSTOCRATE`, `REMOVEALLCOMPS` — yani **ekleme yolları açıldı, silme ve
addon yolları kapalı.**

### 2.5 `sphere.ini` — 58 / 85 (en zayıf ölçülen alan)

Source-X `CServerConfig::sm_szLoadKeys` 279 anahtar tanımlıyor; SphereNet
162'sini tanıyor. Tanınan anahtarların hangisinin gerçekten davranışa
bağlandığı ayrı bir ölçüm:
[ini anahtar sınıflandırması](INI_ANAHTAR_SINIFLANDIRMASI_TR.md). Eksiklerin ~35'i .NET yeniden yazımında **anlamsız** (`NTSERVICE`,
`MYSQLTICKS`, `NETWORKTHREADPRIORITY`, `USEASYNCNETWORK`, `USEEXTRABUFFER`,
`FORCEGARBAGECOLLECT`, `MAXSIZECLIENTIN/OUT`, `BUILDNUM`, `STRIPPATH`, …).
Onlar düşülünce oyun-anlamlı kapsam ~%67.

**Oyun davranışını doğrudan değiştiren, tanınmayan anahtarlar:**

`RUNNINGPENALTY`, `RUNNINGPENALTYOVERWEIGHT`, `STAMINALOSSATWEIGHT`,
`STAMINALOSSOVERWEIGHT`, `BACKPACKOVERLOAD`, `DRAGWEIGHTMAX`, `MOUNTHEIGHT`,
`MEDITATIONMOVEMENTABORT`, `MAGICUNLOCKDOOR`, `SPELLTIMEOUT`,
`NPCCANFIZZLEONHIT`, `NPCSHOVENPC`, `LOSTNPCTELEPORT`, `NPCTRAINPERCENT`,
`OVERSKILLMULTIPLY`, `SKILLPRACTICEMAX`, `HITSHUNGERLOSS`, `WOOLGROWTHTIME`,
`EXPERIENCESYSTEM`/`EXPERIENCEMODE`/`EXPERIENCEKOEFPVM`/`EXPERIENCEKOEFPVP`,
`LEVELSYSTEM`/`LEVELMODE`, `REVEALFLAGS`, `EMOTEFLAGS`, `STATSFLAGS`,
`AREAFLAGS`, `AUTOPRIVFLAGS`, `DISTANCEFORMULA`, `MAXHOUSESGUILD`,
`MAXSHIPSGUILD`, `AUTOHOUSEKEYS`/`AUTOSHIPKEYS`/`AUTONEWBIEKEYS`,
`VENDORMAXSELL`, `PAYFROMPACKONLY`, `TRADEWINDOWSNOOPING`,
`CANUNDRESSPETS`/`CANPETSDRINKPOTION`, `NORESROBE`, `NOWEATHER`,
`TELEPORTEFFECT*`/`TELEPORTSOUND*` (6 anahtar), `FLIPDROPPEDITEMS`,
`ITEMTIMERS`, `MAXPOLYSTATS`, `ZEROPOINT`, `DECIMALVARIABLES`,
`CHATSTATICCHANNELS`, `MEDIUMCANHEARGHOSTS`, `SUPPRESSCAPITALS`,
`SPEECHOTHER`, `WOP*` ailesi.

Bu, tek kategoride en yüksek getirili iş: mevcut bir shard'ın `sphere.ini`'si
sessizce yok sayılan satırlar içeriyor ve davranış farkı buradan doğuyor.

### 2.6 AOS bileşen-prop sistemi — ~55 / 80

Source-X `CCProps*` tabloları 219 giriş / **139 ayrı özellik** taşıyor (giriş
sayısının yüksek olması, aynı özelliğin birden çok bileşen tablosunda
tanımlanmasından). SphereNet 77'sini tanıyor — ve tanıdıkları **doğru olanlar:**
`RES*`/`RES*MAX` (5 element + tavan), `DAM*` dağılımı,
`HIT*` on-hit efekt ailesi (14 adet), `HITAREA*`, `REGEN*`/`REGENVAL*`,
`BONUS*` stat/hits/mana/stam, `FASTERCASTING`/`FASTERCASTRECOVERY`,
`INCREASEDAM`/`INCREASEHITCHANCE`/`INCREASEDEFCHANCE`/`INCREASESWINGSPEED`,
`SLAYER_*`, `FACTION_*`, `LUCK`, `NIGHTSIGHT`, `REFLECTPHYSICALDAM`,
`WEIGHTREDUCTION`, `AMMO*` ailesi, `RANGE`/`RANGEH`/`RANGEL`.

Eksik 62'nin tamamı SE/ML/SA/TOL çağı: `ASSASSINHONED`, `BONEBREAKER`,
`SPLINTERING`, `SEARING`, `BATTLELUST`, `MYSTICWEAPON`, `MAGEWEAPON`,
`BALANCED`, `USEBESTWEAPONSKILL`, imbuing/reforging alanları vb.

Bu bir eksik değil, **hedef sürüm kararı**. Sphere 56x uyumluluğu hedefiyse
kategori fiilen tamamlanmıştır; tam Source-X paritesi hedefse ~%55'te.

### 2.7 Guild stone menüleri — ~63 / 70

Guild'in kendisi çalışıyor (üyelik, ittifak, savaş, kanal konuşması `0xAE`
tip `0xD`/`0xE` ile). Eksik olan **stone gump menü ağacı**: `MASTERMENU`,
`VIEWROSTER`, `VIEWCANDIDATES`, `ACCEPTCANDIDATE`, `REFUSECANDIDATE`,
`RECRUIT`, `DISMISSMEMBER`, `DECLAREFEALTY`, `GRANTTITLE`, `SETCHARTER`,
`SETABBREVIATION`, `SETGMTITLE`, `SETNAME`, `VIEWENEMYS`, `VIEWTHREATS`,
`RETURNMAINMENU` — 30 stone fonksiyonundan **11'i** yok: `ACCEPTCANDIDATE`,
`GRANTTITLE`, `REFUSECANDIDATE`, `RETURNMAINMENU`, `SETCHARTER`, `SETGMTITLE`,
`VIEWCANDIDATES`, `VIEWCHARTER`, `VIEWENEMYS`, `VIEWROSTER`, `VIEWTHREATS`.
Hepsi gump menü ağacına ait; lonca motorunun kendisi çalışıyor.

### 2.8 Chat — 70 / 75

SphereNet 304 satır; Source-X `CChat` + `CChatChannel` + üye sınıfları ~1.300.
Conference chat (`0xB3` alt-komutlar, `0xB5` pencere açma, kanal listesi)
çalışıyor. `0xB2` legacy text-in kabul edilip yok sayılıyor, `0xF9` (KR varyantı)
portlanmadı — ikisi de bilinçli erteleme.

---

## 3. Sadakat tarafı — "düzgün port edilmiş mi?"

Kapsam sayılabilir; sadakat sayılamaz. Bu bölüm sadakat puanlarının dayanağı.

### 3.1 Lehte olan kanıtlar

**Sistematik "uyduruk değer" denetimi yapılmış ve büyük ölçüde kapatılmış.**
2026-07-14'te motorda Source-X atfı olmayan gömülü sabitler taranmış; 5 dalga
düzeltme uygulanmış. Kapatılanlardan bazıları:

| Uyduruk davranış | Düzeltme |
|---|---|
| DAM'sız silah hasarı = `BaseId/10` | Source-X `Weapon_GetAttack`: attackBase = 0 |
| Zehir tik tablosu 5/8/12/16/20 | OSI 3/3/6/6/8 (`CCharAct.cpp:4227`) |
| Tamir sentetik 50 dayanıklılık yazıyordu | `Use_Repair` birebir portu (SKILLMAKE, 1/6–1/3 fail tablosu) |
| Yer item decay 10 dk | 30 dk (`m_iDecay_Item`) |
| Container cap 500 (ve çelişik ikinci cap 125) | 255 (`MAX_ITEMS_CONT`) |
| Sentetik NPC loot (stat-tier → altın/reagent/gem) | kaldırıldı; script loot otorite |
| Summon statları düz sabit | chardef'ten okunuyor |
| Craft exceptional %20 dayanıklılık bonusu | `Skill_MakeItem` band tablosu |
| Spawner delay 60–300 sn | Source-X `rand 1–30 dk` (`CCSpawn.cpp:554`) |

Denetim ayrıca **yanlış alarmları da dokümante etmiş** (`web HP 60+rand250`,
`flee 20 adım`, `güreş baz hız 50`, `healing zorlukları`, `Focus /100 /200` —
hepsi Source-X'in kendisi, atıf yorumu eklenmiş). Bu, denetimin ciddi
yapıldığının işareti: "şüpheli"yi "hatalı" ile karıştırmamış.

**Kod, referansı satır düzeyinde alıntılıyor.** Örnek — `SkillEngine.cs`
skill kazanımı, `CChar::Skill_Experience` portu:

- safe area'da kazanım yok
- `difficulty × 10`, `[1, 1000]` clamp — **işaret korunuyor** (başarısız kullanım
  negatif gelir, 1'e clamp'lenir; `Abs()` alınmaz)
- `GAINRADIUS` sadece skill def'te tanımlıysa aktif — **uydurma varsayılan yok**
- `ADV_RATE` eğrisi yoksa **kazanım yok** (`CValueDefs.cpp:175` → 0), yerine
  uydurma eğri konmuyor
- `@SkillGain` kazanım zarından **önce** ateşleniyor, chance ve cap
  `ref` ile geri okunuyor
- decay zarı gain zarından önce ve toplam cap'ten bağımsız

**Regresyon zırhı gerçek.** 3646 test / 454 test dosyası. Bunların bir kısmı
"test" değil **guardrail**: `TriggerCoverageGuardrailTests` ateşlenmeyen trigger
kümesini her koşuda kaynaktan yeniden türetip dokümante backlog'a karşı
doğruluyor — yeni bir enum üyesi eklenip bağlanmazsa test kırılıyor.
`SourceXVerbInventoryGuardrailTests` Source-X'in kendi `.tbl` dosyalarını ve
`sm_szVerbKeys` tablosunu pinliyor; upstream yüzey büyürse CI görüyor.
Bu, paritenin **folklor** olmasını engelleyen mekanizma.

**Gerçek veriyle doğrulanmış.** Klasik 56T save yükleniyor (2.660 NPC +
53 spawner), harici script paketi çalışıyor, `docs/reviews/` altında 100 saha
inceleme dosyası var, 649 commit.

### 3.2 Aleyhte olan kanıtlar

- **NPC AI tik temposu bilinçli sapma.** Source-X modeli
  `(1+t)×100ms, t = max(0,(150-dex)/2) tenth, rand(t/2..t)`
  (`CCharNPCAct.cpp:2388`). SphereNet'inki saha ayarlı (hareket takılması
  denetiminden). Oyun-içi doğrulama olmadan geri portlanmamalı — ama parite
  açısından açık bir sapma.
- **Bilinçli sapma listesi var.** Örn. spawn'ın gem üstünde doğması
  (`MOREZ` sadece dolaşma tasması), Discordance 20 sn motor fallback'i.
  Bunlar dokümante ama parite denetiminde "hata" gibi görünüp geri
  "düzeltilme" riski taşıyor.
- **Kalan uyduruk-değer kuyruğu:** skillclass 225/7000 fallback,
  `ContainerMaxWeight` 400 (Source-X karşılığı doğrulanmadı),
  REGIONRESOURCE tanımsızken devreye giren fallback gathering ekonomisi.
- **Kapsam ölçümünün kendisi bir üst sınır.** "İsim çözülüyor" ile
  "Source-X ile aynı sonucu veriyor" arasındaki farkın tamamı ölçülmedi;
  ölçülen kısımda (skill gain, combat C1–C7, death/corpse, housing/movement,
  trigger arg/return) sadakat yüksek çıktı, ölçülmeyen uzun kuyruk açık.

---

## 4. Sonuç: emülatör mü, prototip mi?

**Emülatör. Prototip aşaması geride kaldı.**

Bir projeyi prototipten emülatöre taşıyan eşikler ve SphereNet'in durumu:

| Eşik | Durum |
|---|---|
| Gerçek client bağlanıp oynanabiliyor mu? | ✅ Login → char select → dünya → hareket → savaş → büyü → craft → save tam zinciri |
| **Kendi** verisi değil, **mevcut** shard verisi çalışıyor mu? | ✅ Klasik mortechUO/56T save (2.660 NPC, 53 spawner) + harici `.scp` paketi |
| Script motoru gerçek içerik koşuyor mu, demo mu? | ✅ 837 girişlik yüzeyin ~670'i, trigger zinciri arg/return semantiğiyle |
| Regresyon zırhı var mı? | ✅ 3646 test yeşil + kaynaktan türeyen guardrail'ler |
| Uzun süreli çalışma / operasyon | ✅ Canlı paket, RAM/GC, host konsol dayanıklılığı, runbook |
| Davranış farkları rastgele mi, dokümante mi? | ✅ Sapmaların çoğu adlandırılmış ve gerekçeli |

Prototipin tanımı "çalışıyor gibi görünen ama veri/ölçek/regresyon karşısında
dağılan" şeydir. Burada tersi var: **ölçek gerçek, veri gerçek, ve sapmalar
sayılmış.**

**Ama nitelemek gerek:** SphereNet bugün **Sphere 56x sınıfı bir emülatör**,
tam Source-X paritesinde bir emülatör değil. Fark tek bir yerde toplanıyor:
AOS/SE/ML/SA çağı uzun kuyruğu (item özellik sistemi, o çağın skill okulları,
bileşen prop'ları). Hedef Sphere 56x uyumluluğuysa proje **~90**; hedef "Source-X'in yaptığı her
şey"se **~82** — ikisi de §0'daki ±%5 bandıyla okunmalı ve bir ölçüm değil,
ölçülmüş paydalar üzerine kurulmuş bir tahmindir.

---

## 5. Sonraki iş için getiri sıralaması

Etki ÷ maliyet oranına göre:

1. **`sphere.ini` anahtar boşluğu.** ~80 oyun-anlamlı anahtar sessizce yok
   sayılıyor. Çoğu tek bir okuma + tek bir kullanım noktası.
   Mevcut bir shard'ın ini'sini olduğu gibi çalıştırmanın önündeki tek engel.
2. **Item ses tablosu** (`DROPSOUND`/`EQUIPSOUND`/`PICKUPSOUND`/
   `DOOROPENSOUND`/`DOORCLOSESOUND`). Tamamı yok, hepsi ucuz, oyuncuya
   doğrudan hissedilir.
3. **Skill sorgu ailesinin kalanı** (`SKILLCHECK`, `SKILLTEST`,
   `SKILLADJUSTED`). `CANMAKE`, `CANMAKESKILL`, `CANCAST`, `SKILLUSEQUICK` ve
   `SKILLBEST` bu raporun ilk sürümünden sonra cevaplandı. Kalanların motorda
   karşılıkları var, sadece script yüzeyine bağlanmamış.
4. **`MODMAXHITS`/`MODMAXMANA`/`MODMAXSTAM`.** Sık kullanılan stat tavanı
   değiştiricileri; kalıcılık kuralına dikkat (türetilmişi değil base'i
   persist et).
5. **Housing script API'sinin silme yolları** (`DELCOMPONENT`, `DELADDON`,
   `DELVENDOR`, `REMOVEKEYS`, `REMOVEALLCOMPS`) ve addon ailesi. Ekleme yolları
   ve `GET*POS` indeksleme geldi; kalan 10 ad ev sistemini script'ten yöneten
   bir paket için gerekli.
6. **Guild stone menü ağacı** (11 fonksiyon). Klasik shard'larda görünür.
7. **Kalan 21 trigger adı.** Artık ölçüldü: referansın trigger adları sahip
   tablosuyla birlikte çıkarılıp bağlam (`SKILL`/`SPELL`/`REGION` öneki), alt
   çizgi yazımı ve `ITEM` aynası eşlendi. Ham ad karşılaştırması 71 diyor, eşleme
   sonrası **21** kalıyor (`TriggerNameMappingTests`). Bu bir **ad** sayımıdır:
   bir adın enumda bulunması ateşlendiğini göstermez, o yüzden neyin gerçekten
   ateşlendiğinin otoritesi `TriggerCoverageGuardrailTests` olmaya devam ediyor.
   Her biri küçük; `@PayGold`, `@SeeHidden`,
   `@RegionResource*` ve `@PetRelease` en çok script'lenenler.
8. **AOS bileşen prop'ları / SA-ML skill okulları.** Sadece hedef sürüm
   Sphere 56x'ten ileri taşınırsa.

---

### Ek — sayıların yeniden üretimi

```bash
# Source-X script yüzeyi (tablo dosyalarından)
grep -oE 'ADD\([^,]+,\s*"[^"]+"\)' oldSphere/Source-X-full/src/tables/*.tbl

# Source-X trigger listesi (248)
grep -oE 'ADD\([A-Za-z0-9_]+\)' oldSphere/Source-X-full/src/tables/triggers.tbl

# sphere.ini anahtar tablosu (279)
sed -n '759,1060p' oldSphere/Source-X-full/src/game/CServerConfig.cpp \
  | grep -oE '\{\s*"[A-Za-z0-9_]+"'

# SphereNet tarafı: test dışı kaynaktaki tüm string literal önekleri
find src -name '*.cs' -not -path '*Tests*' -not -path '*/obj/*' \
  | xargs cat | grep -oE '"[^"]*"'
```
