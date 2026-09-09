# Port planı — çalışma sırası ve kaldığımız yer

Bu dosya, birden çok oturuma yayılan port/parite çalışmasının **devam noktasıdır**.
Yeni bir oturum buradan başlar: "Sıradaki iş" bölümünü oku, o maddeyi yap, bitince
buradaki kutuyu işaretle ve "Son durum" satırını güncelle.

- Bulgu ve kanıt raporları: `docs/reviews/` (Git ignore altında, yerel).
- Kapanan bulguların kalıcı kaydı: [INCELEME_DOGRULAMA_PLANI_TR.md](INCELEME_DOGRULAMA_PLANI_TR.md).
- Kullanıcıya dönük değişiklik kaydı: `CHANGELOG-EN.txt` / `CHANGELOG-TR.txt`.
- Bu dosya yalnızca **sıra ve durum** tutar; bulgu metnini buraya kopyalama.

## Son durum

| Alan | Değer |
|---|---|
| Son güncelleme | 2026-09-08 |
| Son commit | `ab045a6` + uydurma ekonomi temizliği |
| Tam test | 3.360 başarılı / 0 başarısız |
| Sıradaki iş | **Yeni inceleme dalgası ya da planın yeni maddesi** |

## Çalışma sırası

Sıra, [port durumu doğrulama planından](reviews/PORT_DURUM_DOGRULAMA_VE_BUYUK_PLAN_TR.md)
(yerel) türetildi; oradaki PLAN-xxx numaraları parantezde. Öncelik ölçütü: önce
kanıtlanmış veri kaybı riski, sonra script sözleşmesi, sonra kapsam genişletme.

- [x] **İŞ-1 — 12W: TIMERF deseni, süre ifadesi ve sorgu** (PLAN-205 kesiti)
  Takip planındaki üç açık madde. Küçük, kodu hazır, referans sözleşmesi net.
  Kabul: `TIMERF STOP` / `ISTIMERF` tam komut deseniyle eşleşir (`*`, `?` dahil,
  argümanlar eşleştirmeye katılır); süre ifadesi çarpma/parantez/boşluk tüketir;
  `ISTIMERF` ilk eşleşmeyi döndürür ve sıfırı geçerli sonuç sayar.

- [x] **İŞ-2 — 56T'nin eşlenmeyen kayıt anahtarları** (PLAN-106) — **KAPANDI**
  Altı dilim: (1) shard'ın skill adları, (2) kaydın türü tanımdan — harita pini/kitap
  sayfası, (3) yapının bölgesi (REGION.TAG.\*), (4) klasik taşın loncası,
  (5) klasik geminin ambarı/iskeleleri, (6) 0.56'nın öldürme sayaçları.
  **Paketsiz ölçümde 17 → doğru ölçümde 0.** 56T dökümü artık hiçbir anahtarı park
  etmeden yükleniyor; ölçüm paket + sunucu kablolamasıyla yapılıyor ve testte kilitli.

- [x] **İŞ-2b — multi'nin TYPE'ı ve sahipsiz gemi** — **KAPANDI**
  Yapının kaydı `[MULTIDEF]` adıyla başlıyor ve ID/TYPE yazmıyor; parçaları sayısal
  başlık kullanıyor. İkisi de çözülmüyordu → tekne grafik 0 / tür Normal. Artık
  ikisi de tanımına çözülüyor ve gemi sahipsiz de olsa kaydoluyor: 7 tekne ambarı ve
  iskeleleriyle geri geldi.

- [x] **İŞ-2c — klasik yapının bölgesi** — **KAPANDI**
  93 yapının hiç bölgesi yoktu (bölge gerçekleştirme ev kaydına bağlıydı). Artık
  multi'nin kendisine bağlı: sahipsiz yapı da ayak izini, adını, olaylarını,
  tag'lerini ve REGION.FLAGS'ini alıyor. **Sahiplik kasıtlı olarak eklenmedi:** 56T
  evleri sahibi MORE1'de tutuyor ama bunu shard'ın kendi scriptleri okuyor; Source-X
  CItemMulti sahibi yalnız OWNER'dan okur, MORE1'i hiç kullanmaz.

- [x] **İŞ-3 — Save→Load→Save alan bazında eşitlik** (PLAN-107) — **KAPANDI**
  `SaveRoundTripParityTests`: kaydet → yükle → kaydet ve iki kaydı satır satır
  karşılaştır; hem motor API'siyle kurulan dünya hem klasik kayıttan yüklenen dünya.
  İlk bulgusu: kendi maksimum canı olmayan karakter `MAXHITS=0` yazıyor, yüklemede
  1'e yuvarlanıyordu → kayıt sabit noktaya ulaşmıyordu; artık yazılmıyor.
  Testin kendisi "her şeyi kaybeden kayıt da sabit noktadır" tuzağına karşı, çevrilen
  verinin kayıtta bulunduğunu ayrıca doğruluyor.

- [x] **İŞ-4 — Tek nesne oluşturma/kopyalama sözleşmesi tablosu** (PLAN-101) — **KAPANDI**
  On giriş noktası altı sütunda karşılaştırıldı (tablo takip planında). İki fark
  kapandı: eşya DUPE'u artık kopyayı çağıranın ACT'ine yazıyor; DUPE sayısındaki
  uydurma 1000 sınırı yerine referansın `MAXITEMCOMPLEXITY` ini ayarı (varsayılan 25,
  yalnız üst düzey nesnede). Üç davranış bilinçli sapma olarak **doğrulandı ve
  değiştirilmedi** (kopya @Create'i tekrar çalıştırmaz; karakter DUPE'u NEW/ACT
  ayarlamaz; SERV. öneki ACT'e dokunmaz).

- [x] **İŞ-5 — Gerçek ini fark listesi** (PLAN-301/302) — **KAPANDI (karar bekleyen 2 madde hariç)**
  Canlı `sphere.ini`'nin 184 anahtarından 33'ü hiç okunmuyordu. Dört dilimde **13'ü
  davranışa bağlandı** (teleport efekt/ses 6'lısı, MAXHOUSESGUILD, NPCCANFIZZLEONHIT,
  BACKPACKOVERLOAD, FLIPDROPPEDITEMS, NPCTRAINPERCENT/NPCTRAINMAX alias'ları,
  MAXITEMCOMPLEXITY İŞ-4'te). Kalanların tam sınıflandırması takip planında.

  **Tüketicisi olmadığı için ayar EKLENMEDİ** (sahte ayar olurdu): SPELLTIMEOUT (hedef imleci süre aşımı yok),
  STATSFLAGS ("0 = türet" semantiği yok). DISTANCEFORMULA ve ağ/harita grubu kapsam
  dışı. HITSUPDATERATE referansta ELEM_VOID, MONSTERTIGHT referansta hiç yok.

  **Karar verilen 2 madde (kullanıcı: "Source-X nasılsa öyle") — KAPANDI:**
  - `NOWEATHER` eklendi, varsayılan referansınki (hava YOK). Bayrak yeni hava atmayı ve
    istemciye söylemeyi durduruyor; scriptin kurduğu hava saklanıp okunabiliyor.
  - `NPCSKILLSAVE` eklendi: dünya okunurken eşik altı NPC skilleri düşürülüyor
    (referansta FixWeirdness); oyuncuya dokunulmuyor.
  Canlı shard'ın ini'si `NOWEATHER=0` / `NPCSKILLSAVE=100` diyor — hava açık kalır.

  `MAXPOLYSTATS` önce "tüketicisi yok" diye park edilmişti; sonra tüketicisi yazıldı:
  polymorph artık formun kendi STR/DEX'ini alıyor (`MAGICF_POLYMORPHSTATS` kapılı,
  değişim bu ayarla sınırlı, büyü hafızasında saklanıp geri alınıyor).

- [x] **İŞ-6 — `Item` içindeki ham `_type` kapıları** — **ÖLÇÜLDÜ, DEĞİŞİKLİK GEREKMEDİ**
  Yaklaşık yirmi okuyucu (yığınlanma, statik engel, tuzak, gemi parçası, ceset, multi
  komutları) türü çözen özellik yerine ham alanı okuyor. Zincir ölçüldü: fabrika yolu
  türü kuruyor, klasik yol TYPE yazmıyor ama yükleme sonrası
  `MaterializeDefinitionType()` dolduruyor. Gerçek 56T dünyasında
  `WorldInvariantAuditor` **0 anomali** veriyor — dolayısıyla kalan ham okuyucular
  doğru. Gerçek veri testi artık bu değişmezi (ve diğer bütün dünya değişmezlerini)
  assert ediyor; iddia varsayım olmaktan çıktı. Gerçek pencere (kayıt OKUNURKEN
  property yüzeyi) İŞ-2 ikinci diliminde `Item.EffectiveType` ile kapanmıştı.
  **Kalan risk, kayıtlı:** çalışma anında `BaseId` ile kurulup türü açıkça
  verilmeyen eşya; denetleyici yalnız yükleme sonrası koştuğu için ancak kaydedilip
  yeniden yüklenince yakalanır.

- [x] **İŞ-7 — Kopya ve kayıt döngüsünde havuz değerleri** (PLAN-102/103) — **KAPANDI**
  13J'nin dört bulgusu kodda kapalıydı; raporun kendi bıraktığı boşluk (**kayıt
  döngüsü hiç denenmedi**) denendi ve **ters yönde** bir hata çıktı: temel değer
  şişmiyor, MEVCUT havuz kayboluyordu. BONUSHITSMAX kıyafetli oyuncu her çıkışta
  120/120 kaydedip 100/120 giriyordu. İki kök sebep: (1) `HITS=` ataması iyileştirme
  gibi kırpılıyordu — referansta atama `Stat_SetVal` (üst sınır yok), oynanış değişimi
  `UpdateStatVal` (kırpar); (2) yükleme ekipmanı iki kez giydiriyor, ikincisi önce
  çıkarıyor ve o an havuzu düşen maksimuma kırpıyor. Ayrıca kayıt sırası referansın
  `MAX*` → `HITS` sırasına alındı.

- [x] **İŞ-8 — Trigger uzun kuyruğu, paket tarafından ölçüm** (PLAN-206) — **KAPANDI**
  Referansın kendi tablosu (248 trigger) + 910 script dosyası / 10.166 kanca tarandı.
  Dokuz ad hiçbir yere ulaşmıyor; beşi + üçü referansta da yok (ölü script), **dördü
  gerçek boşluk**. İlk statik geçişin 19 adayının çoğu yanlış alarmdı: skill/bölge/büyü
  aşamaları ve `@item<Ad>`/`@char<Ad>` aynaları enum değerinden geçmeden ateşleniyor.
  `DispatchableTriggerNames` + kendini bakım eden korkuluk testi eklendi.

- [x] **İŞ-9 — Dört gerçek trigger boşluğu** (PLAN-206 devamı) — **KAPANDI**
  `@RegionResourceFound` + `@ResourceFound` (damar bulunduğunda, tek dönüş değeri
  paylaşımlı), `@RegionResourceGather` (toplayanda, `@ResourceGather` ile yan yana) ve
  `@HitReactive`. Yanında reaktif zırhın üç kusuru düzeldi: yansıyan pay artık uydurma
  `damage / 4` değil büyü tanımının EFFECT eğrisinden; darbe artık AZALTILIYOR (önce
  yalnız yansıtılıyordu); iki karelik mesafe şartı kondu.

- [x] **İŞ-10 — Yansıma ailesi yalnız yakın dövüşteydi** — **KAPANDI**
  Aile ortak adımlara çıkarıldı (`IsReflectableBlow` / `ApplyReactiveArmor` /
  `ApplyBloodOathAndSuitReflect`) ve DAMAGE verb yolu da çağırıyor. Kapsam ölçerken
  daraldı: referansta aile **fiziksel darbeye** özel, yani büyü hasarı orada da
  sekmiyor; faili olmayan tuzak/alan da dışarıda. 11 hasar noktamızdan gerçek boşluk
  yalnız DAMAGE verb yoluydu.
  **Kalan yapısal not:** referansta tek `OnTakeDamage` kapısı var, bizde 11 ayrı hasar
  uygulama noktası. Davranış paritesi sağlandı; birleştirme ayrı bir iş.

- [x] **İŞ-11 — Stat modifier ailesi ve OSTR** (PLAN-303) — **KAPANDI**
  İki bulgu. (1) `OSTR/ODEX/OINT` ayrı alandı; kayıtta `STR`'den sonra yazıldığı için
  bayat gölge yüklemede kazanıyordu — **klasik kayıttan gelen her karakter her
  restart'ta antrenmanını kaybediyordu** (ölçüm: 110→100). Artık temelin takma adı.
  (2) `MODSTR/MODDEX/MODINT` yazılıp hiç okunmuyordu (canlı paket 83 kez yazıyor);
  artık etkin stata katılıyor ve kaydediliyor. `<STR>` ayarlanmış, `<OSTR>` temel.
  **Kapsam dışı:** `MODMAX*` ailesi referansta var, bizde yok — ama hiçbir paket/kayıt
  kullanmıyor (ölçüm 0).

- [x] **İŞ-12 — Eşya sesleri** (PLAN-305) — **KAPANDI**
  Planın kendi ifadesiyle "property varlığı ile ses çalmanın varlığını ayır": ikisi de
  eksikti. **Kuşanma sesi hiç çalınmıyordu** (referans görünür katmanda 0x057) ve
  bırakma sesi tek sabitti (referans: tür tablosu → `DROPSOUND` → yedek, yedeği de
  "üstüne mi yere mi" düştüğüne bağlı). Altın sesleri uydurmaydı, referansa alındı.
  `DROPSOUND`/`EQUIPSOUND` artık instance→ITEMDEF sırasıyla okunuyor.

- [x] **İŞ-13 — CANMAKE / CANMAKESKILL** (PLAN-304) — **KAPANDI**
  Ölçüm planı doğruladı: `CANCAST` var (66 kullanım), `CANMAKE`/`CANMAKESKILL` hiç
  yoktu (8 kullanım). İkisi de referansın `Skill_MakeItem` SELECT aşamasından:
  CANMAKESKILL = beceri + SKILLMAKE, CANMAKE = onlar + malzeme. Çalışma yeri bilerek
  cevabın dışında (referans ocağı becerinin kendi aşamasında arar).
  **Ertelendi:** `SKILLUSEQUICK`/`SKILLTEST` — sorgu görünümlü ama yan etkili
  (deneme sayılır, kazanç tetikler); ayrı ve dikkatli bir iş.

- [x] **İŞ-14 — SKILLUSEQUICK** (PLAN-304 kuyruğu) — **KAPANDI**
  İŞ-13'te bilerek ertelenen madde. Sorgu gibi duruyor ama zar atıyor, beceri
  yükseltiyor ve `@SkillUseQuick` çalıştırıyor. Referansın iki tuhaflığı korundu:
  üçüncü argüman **ters** (sıfır değilse çan eğrisi kapalı) ve iki argümandan azı
  anahtarı **yanıtsız** bırakıyor. `SKILLTEST` kapsam dışı (paketlerde 0 kullanım).

  **Ölçülüp boşluk çıkmayanlar (aynı tur):** "yazılıp hiç okunmayan durum" taraması
  (4 aday, 4'ü de yanlış pozitif) ve ölüm anında imleçteki eşya (zaten doğru,
  `DeathInventoryEdgeTests` sabitliyor). Ayrıntı takip planında.

- [x] **İŞ-15 — Verb uzun kuyruğu, paket tarafından ölçüm** — **KAPANDI**
  İŞ-8'in verb karşılığı: 1.881 dosya, 14.223 ifade, 314 farklı başlık. Motorda izi
  olmayan 121 adayın neredeyse tamamı shard'ın kendi `[FUNCTION]`'ları; referans
  tablolarına göre **gerçek verb sayısı iki**: `MESSAGE`/`MSG` (330 kullanım, bizde
  yalnız konsol komutuydu) ve `ADDCIRCLE` (9 kullanım). İkisi de bağlandı.

- [x] **İŞ-16 — Uydurma-değer backlog'unun ölçülebilir kalanları** — **KAPANDI**
  (1) Toplama fallback ekonomisi (gömülü cevher/balık/kütük + kendi marker havuzu)
  silindi; backlog'un kendi koşulu ölçülerek doğrulandı — canlı paket t_rock/t_water/
  t_tree'yi RESOURCES ile tanımlıyor, yani dal zaten ölüydü. (2) `ContainerMaxWeight`
  400 → 0: referansta sandık/çanta için global tavan yok, sınır kabın kendi
  MODMAXWEIGHT'i (varsayılan 0). **Banka sınırı referansta gerçek, dokunulmadı.**

  **Sonuçsuz ölçüm (kayıtlı):** property OKUMA taraması. Statik yöntem bu ayakta
  güvenilir aday üretmiyor (ad-uzayı ve argümanlı anahtar kusurları); doğru araç
  motordaki `DebugUnresolved` ile çalışma zamanı ölçümü.

## Yapıldı

Bu bölüm yalnızca bu plandaki işlerin kapanışını listeler; bulgu ayrıntısı takip
planındadır.

- **İŞ-16 KAPANDI** — 2026-09-09. İki uydurma ekonomi kaldırıldı (toplama fallback'i,
  düz 400 kap tavanı). Tam suite 3.360.
- **İŞ-15 KAPANDI** — 2026-09-09. MESSAGE/MSG nesne verb'i + ADDCIRCLE. Test:
  `ObjectMessageVerbTests` (5), `AddCircleVerbTests` (5). Tam suite 3.360.
- **İŞ-14 KAPANDI** — 2026-09-09. SKILLUSEQUICK bağlandı. Test:
  `SkillUseQuickPropertyTests` (7). Tam suite 3.350.
- **İŞ-13 KAPANDI** — 2026-09-09. CANMAKE/CANMAKESKILL bağlandı. Test:
  `CanMakeQueryParityTests` (6). Tam suite 3.343.
- **İŞ-12 KAPANDI** — 2026-09-09. Eşya bırakma/kuşanma sesleri referansa alındı;
  kuşanma sesi hiç yoktu. Test: `ItemSoundParityTests` (13). Tam suite 3.337.
- **İŞ-11 KAPANDI** — 2026-09-09. Stat modifier + OSTR. Test:
  `StatModifierParityTests` (7); üç düzeltme tek tek geri alınıp doğrulandı. Ayrıca UOP
  harita geçici dosyası sızıntısı kapatıldı (makinede 120 GB birikmişti). Tam suite
  3.324.
- **İŞ-10 KAPANDI** — 2026-09-09. Yansıma ailesi scriptli hasarda da çalışıyor. Test:
  `ScriptedDamageReflectTests` (6). Tam suite 3.317.
- **İŞ-9 KAPANDI** — 2026-09-09. Dört trigger + reaktif zırh yeniden kuruldu. Test:
  `ResourceAndReactiveTriggerTests` (12); korkuluğun boşluk listesi boşaldı. Tam suite
  3.311.
- **İŞ-8 KAPANDI** — 2026-09-08. Trigger kapsamı paket tarafından ölçüldü; dört gerçek
  boşluk kayda geçti, yanlış alarmlar elendi. Test: `ScriptPackTriggerCoverageTests` (2).
  Tam suite 3.299.
- **İŞ-7 KAPANDI** — 2026-09-08. Havuz kayıt döngüsü: atama/oynanış ayrımı, çifte
  giydirme, kayıt sırası. Test: `PoolRoundTripParityTests` (6); üç düzeltme tek tek
  geri alınıp yakalandığı doğrulandı. Tam suite 3.297.
- **İŞ-6 ÖLÇÜLDÜ** — 2026-09-08. Ham `_type` okuyucuları için değişiklik gerekmedi;
  gerçek shard yüklemesinde 0 ıraksama. Gerçek veri testi bütün dünya değişmezlerini
  assert ediyor (önce yalnız yükleyip anahtar sayıyordu). Tam suite 3.291.

- **İŞ-5 KAPANDI** — 2026-09-08. 33 okunmayan anahtarın 13'ü bağlandı, geri kalanı
  gerekçesiyle sınıflandırıldı (tüketici yok / referansta no-op / kapsam dışı / karar
  bekliyor). Tüketicisi olmayan anahtara ayar eklenmedi.
- **İŞ-5 ilk dilim** — 2026-09-07. Canlı ini'nin 184 anahtarından 33'ünün hiç
  okunmadığı ölçüldü ve sınıflandırıldı; teleport efekt/ses altılısı davranışa
  bağlandı. Tam suite 3.279.
- **İŞ-4 KAPANDI** — 2026-09-07. On giriş noktası altı sütunda tabloya döküldü;
  eşya DUPE'unun ACT'i ve uydurma 1000 sınırı (→ `MAXITEMCOMPLEXITY`) düzeltildi,
  üç sapma doğrulanıp korundu. Tam suite 3.278.
- **İŞ-3 KAPANDI** — 2026-09-07. `SaveRoundTripParityTests` (kaydet→yükle→kaydet,
  satır satır karşılaştırma; motor dünyası + klasik kayıt dünyası). İlk bulgusu
  `MAXHITS=0` sapmasıydı, düzeltildi. Tam suite 3.273.
- **İŞ-2c KAPANDI** — 2026-09-07. Yapının bölgesi ev kaydına değil multi'ye bağlı;
  56T'nin 93 yapısı artık bölgesiyle (bayraklar, olaylar, tag'ler) yükleniyor.
  Test: `LegacySaveKeyParityTests` +2; tam suite 3.271.
- **İŞ-2b KAPANDI** — 2026-09-07. Yapının kaydı `[MULTIDEF]`'e, parçaları sayısal
  başlığa çözülüyor; sahipsiz gemi de kaydoluyor. 56T'de 7 tekne ambarı+iskelesiyle
  geri geldi. Test: `LegacySaveKeyParityTests` +3; tam suite 3.269.
- **İŞ-2 KAPANDI (PLAN-106)** — 2026-09-07. Son dilim: 0.56'nın KILLSPLAYER'ı
  referansın tek sayacına (KILLS) çevriliyor, KILLSNPC tag olarak korunuyor
  (referansta karşılığı yok). 56T dökümünde eşlenmeyen anahtar **0**; 834 karakter
  cinayet sayısını geri aldı. Tam suite 3.266.
- **İŞ-2 beşinci dilim (PLAN-106)** — 2026-09-07. Klasik geminin HATCH/PLANK
  satırları okunuyor ve gemi katmanına bileşen olarak kaydediliyor; klasik "temizlenmiş
  uid" işaretçisi için `Serial.NamesAnObject` eklendi. Ölçüm 4 → 2. Test:
  `LegacySaveKeyParityTests` +8; tam suite 3.265.
- **İŞ-2 dördüncü dilim (PLAN-106)** — 2026-09-07. Klasik lonca/şehir taşının
  ALIGN/ABBREV/CHARTER/MEMBER satırları motorun GUILD.* biçimine çevriliyor; gerçek
  veride 10 lonca geri geldi (en büyüğü 13 üye). Taş kendi adını veriyor. Ölçüm
  8 → 4. Test: `LegacySaveKeyParityTests` +4; tam suite 3.257.
- **İŞ-2 üçüncü dilim (PLAN-106)** — 2026-09-07. Multi kaydındaki
  `REGION.TAG.<ad>` satırları okunuyor, geri yazılıyor ve ev/gemi bölgesi
  gerçekleşirken bölgeye kopyalanıyor (93+1 kayıt). Okuma bilinçli olarak eşyada
  yakalanmadı — bölgeye gidiyor. Ölçüm 10 → 8. Test: `LegacySaveKeyParityTests` +3;
  tam suite 3.253.
- **İŞ-2 ikinci dilim (PLAN-106)** — 2026-09-07. Kitap/harita/gemi property
  kapıları ham `_type` yerine etkin türe (`Item.EffectiveType`) bakıyor; klasik
  kayıtta TYPE satırı olmadığı için 59 harita pini ve 18 kitap sayfası okunmuyordu.
  Gerçek veri geçişi sunucu kablolamasını aynalıyor. Ölçüm 15 → 10. Test:
  `LegacySaveKeyParityTests` +3; tam suite 3.250.
- **İŞ-2 ilk dilim (PLAN-106)** — 2026-09-07. Shard'ın kendi skill adları
  (`[SKILL n] KEY=`) yükleme ve script yollarında çözülüyor; üç ayrı isim tablosu
  `SkillNames`te birleşti; `Farming` adını kapan NEWBIE kaynağı yüzünden defname
  tablosu yerine skill blokları kendi adlarıyla indeksleniyor. Gerçek veri ölçümüne
  paketli geçiş eklendi (17 → 15 anahtar). Test: `LegacySaveKeyParityTests` (5) +
  paketli 56T geçişi; tam suite 3.247.
- **İŞ-1 (12W)** — 2026-09-07. `SpherePattern` (Str_Match portu) ve
  `ScriptNumber.TryEvaluatePrefix` eklendi; `ClearTimerF`/`GetTimerFRemaining`
  komutun tamamıyla eşleşiyor, `ISTIMERF` ilk eşleşmeyi dönüyor, TIMERF süresi
  ifade olarak tüketiliyor. Test: `DelayedCallParity12WTests` (40); tam suite
  3.241. Düzeltmeler geçici geri alınıp 12 testin eski davranışı yakaladığı
  doğrulandı. Ayrıntı takip planında 12W bölümünde.

## Canlı sunucu ini'si — güncellendi (8 Eylul 2026)

`C:\sphereNetServer\sphere.ini` kullanıcı onayıyla repo ini'siyle aynı hâle getirildi;
yedek: `sphere.ini.bak-20260908`. Değişenler: TELEPORTEFFECT/SOUND × 3 (0 → referans
değerleri), MAXHOUSESGUILD 0→1, BACKPACKOVERLOAD 0→40, FLIPDROPPEDITEMS 0→1,
NPCCANFIZZLEONHIT 1→0, MAXITEMCOMPLEXITY=25 satırı eklendi. **Dokunulmayanlar:**
`NOWEATHER=0` ve `NPCSKILLSAVE=100` — shard'ın kendi tercihi.

## Oturum notları

- 2026-09-07: Plan kuruldu. 12W öncesi durum: takip planı 352 kapalı / 3 açık,
  tam test 3.201. Port durumu doğrulama planının Çevrim 1'i (PLAN-001..005'in
  kayıt eşleme kısmı) `3cd431b` ve `8611064` ile fiilen kapandı; PLAN-102/103 ve
  PLAN-202 ile PLAN-203'ün yarısı da 13I/13J dalgasında kapandı.
