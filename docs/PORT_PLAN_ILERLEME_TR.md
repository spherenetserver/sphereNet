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
| Son güncelleme | 2026-09-14 |
| Son commit | `d96ee36` + İŞ-86 (spawner ölümü olayla duyuyor) |
| Tam test | 3.887 başarılı / 0 başarısız (79 veri kapısı, 1'i verisiz) |
| Sıradaki iş | **B1-B8, B12 kapandı; B9/B10/B11 yarım.** Sıradaki: B9/B10/B11 kalanları |

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

- [x] **İŞ-17 — Kabın kendi ağırlık sınırı** — **KAPANDI**
  `MODMAXWEIGHT` artık eşyada da çalışıyor (referansta ortak nesne tabanında) ve
  kaydediliyor. Düz 400 tavanı kalktığına göre bir kabı sınırlayan tek şey bu.
  **Property okuma ölçümü ikinci kez sonuçsuz:** çalışma zamanı denemesi motorun
  eksiğini değil bench'in bağlamsızlığını ölçtü (SRC/DLOCAL/ACT...). Doğru yol gerçek
  dünyada gerçek trigger koşturan bir akış tezgahı; ayrı ve büyük iş, kayıtlı.

- [x] **İŞ-18 — Vendor ve secure trade uçtan uca** (PLAN-401) — **KAPANDI**
  Dalga 4'ün ilk maddesi. Sekiz ayağın altısı ölçülüp **zaten doğru** çıktı
  (fiyat/miktar satır bazında, dolu çanta ağırlık+slot, iç içe altın özyinelemeli,
  teklif değişimi kap değişmezi olarak, disconnect ve ölüm kablolu; takas penceresinin
  ağırlığı da referanstaki gibi sahibine yazılmıyor). **Yeniden yükleme ayağı üç
  gerçek boşluk verdi:**
  1. **Eşyanın kendi TYPE'ı kaydı aşmıyordu.** Referans, tür tanımınkinden farklıysa
     TYPE yazar (CItem.cpp:2461); bizde yalnız Multi/MultiCustom/Ship için yazılıyordu,
     yani scriptin ya da motorun kurduğu her tür restart'ta ITEMDEF'e dönüyordu.
     Ölçüm (56T, 76.359 eşya): 2.383 eşya kendi türünü taşıyor ve bunlar tam olarak
     kaydında zaten TYPE satırı olanlar → kayıt büyümüyor, test bu turu kilitliyor.
  2. **Takas ortasında alınan kayıt malı yiyordu.** Referans yüklemede pencereyi
     dağıtır (FixWeirdness 0x2220): içindekiler sahibinin çantasına, pencere silinir.
     Bizde Special katmanında erişilemez bir kapta kapalı dönüyordu.
  3. **@TradeAccepted malı adlandırmıyordu.** Referans alınacak eşyaları REF1..REFn
     olarak verir (CItemContainer.cpp:196), bizde yalnız ARGN1 sayısı geçiyordu —
     Scripts-X-main housing paketi tapuyu bu listede arıyor, yani takas edilen ev hiç
     el değiştirmiyordu. REF haritası tetikleyici zinciriyle paylaşılıyor.

- [x] **İŞ-19 — Dövüş uçtan uca, hedef değişimi ve tehdit** (PLAN-402 birinci dilim) — **KAPANDI**
  PLAN-402'nin "hedef değişimi" ayağı. Ölçüm iki taraftan yapıldı: canlı paketin
  `ATTACKER.*` kullanımı ve referansın `Fight_Attack` → `Attacker_Add` →
  `NPC_FightFindBestTarget` zinciri. **Beş boşluk:**
  1. **Script yüzeyi.** Canlı paket `<ATTACKER.0>`, `<ATTACKER.LAST.DAM>`,
     `<ATTACKER.MAX.DAM>` okuyor ve `ATTACKER.CLEAR` yazıyor; **dördü de**
     çözülmüyordu. Referansın seçici→satır→alan modeli kuruldu.
  2. **Liste sırası.** Darbe alan satır sona taşınıyordu → `ATTACKER.n` her
     darbede başkasını gösteriyor. Referans yalnız sona ekler; LAST damgadan.
  3. **Liste tek yönlüydü.** Referansta saldırdığın hedef de kendi listene yazılır;
     bizde yalnız alınan hasar dolduruyordu, dolayısıyla hedef ölünce sıradaki
     rakip listede yoktu ve motor tüm görüş menzilini yeniden tarıyordu.
  4. **THREAT yoktu.** Yerinde `TotalDamage/2` uydurma bonusu vardı. Gerçek model:
     saklanan değer, script/emir yazar, `NPC_AI_THREAT` ile en yüksek tehdit kazanır,
     sahibin emri 1000+en yüksek taşır, oyuncuda hiç tutulmaz, kayda yazılır.
  5. **@Attack argümansızdı.** ARGN1 tehdit / ARGN2 yoksay + geri yazma + RETURN 1;
     @CombatAdd de aynı sözleşmeye alındı.
  Yanında: öldürme kredisi artık hasar istiyor (referansın `amountDone > 0` kapısı),
  liste artık yalnız vuranları tutmadığı için taşıyıcı.

- [x] **İŞ-20 — Dövüş: swing durumu ve @HitCheck sözleşmesi** (PLAN-402 ikinci dilim) — **KAPANDI**
  PLAN-402'nin "menzil/LOS, mühimmat, timer" ayakları **ölçülüp zaten doğru**
  bulundu (C-dalgalarının kapsamı): min/max menzil, LOS, yay+kalkan, okçuluk
  hareket gecikmesi, gemi kuralı, paralize, ok ekonomisi (iska %40 yere, isabette
  NPC gövdesine, `LOCAL.Arrow`/`ArrowHandled`, NPC iskada ok harcamaz). **Üç gerçek
  boşluk swing DURUMUNDA çıktı:**
  1. **Trigger RETURN'ü sayı değildi.** Dogru/yanlış sonuç -1 / -2 taşıyamıyor.
  2. **@HitCheck ters okunuyordu.** Her doğru dönüş ZORUNLU ISKA sayılıyordu;
     referansta RETURN 1 = "durum ARGN1'de", -1 = geçersiz hedef, -2 = gömülü yolu
     yine de çalıştır. Referans paketinin kendi dövüş katmanı `argn1 SWING_READY /
     return 1` ile *bekle* diyor — bizde her bekleme bir vuruş+ıska oluyordu.
  3. **`<SWING>` yoktu.** Canlı paketin prop dialogu okuyor; yalnız uydurma
     `SWINGSTATE` adı yanıt veriyordu. Yazma tarafı da eklendi (-1..2 dışını reddeder).

- [x] **İŞ-21 — Dövüş: @HitParry sözleşmesi** (PLAN-402 üçüncü dilim) — **KAPANDI**
  PLAN-402'nin "parry/yansıma" ayağı. Yansıma İŞ-9/İŞ-10'da kapanmıştı; savuşturma
  tarafı açıktı ve **ARGN1 ters anlamdaydı**. Dört boşluk: (1) trigger zardan SONRA
  ateşleniyordu, yani `LOCAL.ParryChance` ulaşılamazdı; (2) ARGN1 "geçen hasar"
  okunuyordu, referansta "düşülen yüzde"; (3) ARGN2/ARGO ve dört LOCAL hiç yoktu,
  RETURN 1 vetosu yoktu; (4) savuşturan eşya hiç yıpranmıyordu. Ayrıca varsayılan
  indirim artık Parrying yeteneğinin **EFFECT eğrisinden** geliyor.

  **PLAN-402 bununla kapandı:** hedef değişimi (İŞ-19), swing durumu/@HitCheck
  (İŞ-20), menzil/LOS + mühimmat + timer (İŞ-20'de ölçüldü, değişiklik gerekmedi),
  parry (İŞ-21), yansıma (İŞ-9/10), ölüm ve trigger veto sırası (D1-D3 + İŞ-19'un
  hasar kapısı).

- [x] **İŞ-22 — Büyü: ellerin boşaltılması** (PLAN-403 birinci dilim) — **KAPANDI**
  PLAN-403'ün "cast başlangıcı" ayağı. Üç boşluk, üçü de **canlı shard'da aktif**
  (`EQUIPPEDCAST=0`): (1) referans elleri boşaltır, biz büyüyü söndürüyorduk —
  elinde silah olan oyuncu hiç büyü yapamıyordu; (2) `CAN_I_EQUIPONCAST` hiç
  okunmuyordu; (3) donmuş büyücü bayrak okunmadan reddediliyordu, yani
  `MAGICF_CASTPARALYZED` ölü bir ayardı.
  Yanında **test altyapısı düzeltmesi**: `ResetEngineStatics` dövüş/büyü
  anahtarlarının bir kısmını sıfırlamıyordu ve sınıf kurucusundaki sabitleme
  Reset'ten önce çalıştığı için siliniyordu.

- [x] **İŞ-23 — Büyü: maliyet ve yürürken cast** (PLAN-403 ikinci dilim) — **KAPANDI**
  PLAN-403'ün "mana maliyeti" ve "hareketle iptal" ayakları. **Ölçülüp zaten doğru
  bulunanlar:** wand mana bedavası ve scroll yarım mana (referansta gerçek, formül
  `Calc_SpellManaCost` içinde), reagent yalnız oyuncunun kendi gücünden castında
  (wand/scroll muaf), hasarla iptalin `[SPELL] INTERRUPT` eğrisi ve
  `NPCCANFIZZLEONHIT`. **Üç boşluk:**
  1. **Yürümek büyüyü iptal ediyordu.** Referansta böyle bir kural YOK: ya
     `FREEZEONCAST` adımı engeller ya da karakter yürür ve büyü sürer. Canlı
     shard'da (MAGICFLAGS=0) tek adımda büyü kaybı demekti; iki bayrak da
     yüklenip hiç uygulanmıyordu.
  2. **`LOWERMANACOST` okunmuyordu** (negatif değer faturayı yükseltir).
  3. **`LOWERREAGENTCOST` okunmuyordu** (kaç tane değil, HİÇ harcamama şansı).
     İkisini de referans script paketi artifact'larında kullanıyor.

- [x] **İŞ-24 — Büyü: bölge kapıları ve ölü bayraklar** (PLAN-403 üçüncü dilim) — **KAPANDI**
  PLAN-403'ün "alan etkisi / summon / seyahat" ayakları. **Üç boşluk:**
  1. **`CheckAntiMagic` yoktu.** Bizde yalnız `NoMagic` ve `NoMagicDamage`
     soruluyordu. Referansın tablosu portlandı; en görünür sonucu: **Mark'ın hiç
     bölge kapısı yoktu** ve **gemi güvertesinde rune işaretlenebiliyordu**.
  2. **`MAGICF_SUMMONWALKCHECK` ölü bayraktı** — yaratık duvarın içine çağrılabiliyordu.
  3. **`MAGICF_OVERRIDEFIELDS` ölü bayraktı** — yeni alan eskisinin üstüne biniyordu.

  **PLAN-403 bununla kapandı:** cast başlangıcı/eller (İŞ-22), maliyet ve yürürken
  cast (İŞ-23), bölge kapıları + summon + alan (İŞ-24); reagent/scroll/wand ve
  hasarla iptal İŞ-23'te ölçülüp doğru bulundu.

- [x] **İŞ-25 — Üretim: başarısızlık bedeli ve ACTIONEFFECT** (PLAN-404 birinci dilim) — **KAPANDI**
  PLAN-404'ün "kısmi maliyet" ayağı. **Ölçülüp zaten doğru bulunanlar:** toplama
  miktarı (`REAPAMOUNT` eğrisi, yoksa `AMOUNT`/2 ve stok kırpması), tool aşınması
  (her denemede), stroke sayıları, `@ResourceGather` sözleşmesi. **İki boşluk:**
  1. **`ACTIONEFFECT` hiç yoktu** — canlı paket prop dialogunda basıyor ve INPDLG
     ile geri yazıyor.
  2. **Başarısız üretimin bedeli yalnız düz zardı** — referansta önce
     `ACTIONEFFECT`, sonra üretim yeteneğinin `EFFECT` eğrisi, en son düz zar.
     Canlı paket Inscription'a `EFFECT=50` veriyor.

- [x] **İŞ-26 — Üretim: kalite duyurusu ve usta imzası** (PLAN-404 ikinci dilim) — **KAPANDI**
  PLAN-404'ün "kalite" ayağı. **Ölçülüp zaten doğru bulunan:** kalite formülünün
  tamamı (logaritmik ±0..2 sapma, yedi bant, bant içi zar) referansla birebir.
  **İki boşluk + bir test kırılganlığı:**
  1. **`MAKESUCCESS_1..6` hiç gönderilmiyordu** — altı mesaj tablodaydı, berbat
     hançerle üstün hançer aynı okunuyordu.
  2. **`OF_NOITEMNAMING` ölü bayraktı** — shard imzaları kapatamıyordu.
  3. **İŞ-25'te kayda geçen smelt kırılganlığı çözüldü:** testin kendisi Mining
     zarını sabitlemiyordu (100.0'da bile çan eğrisi).

- [x] **İŞ-27 — Kaynak: boş çıkan nokta boş kalır** (PLAN-404 üçüncü dilim) — **KAPANDI**
  PLAN-404'ün "kaynak tükenmesi/yenilenmesi" ve "retry" ayakları. **Ölçülüp zaten
  doğru bulunanlar:** damar ömrü (REGEN tenths, tek pencere, yeniden kurulmaz),
  havuz miktarı (AMOUNT rastgele + Workhorse ırk bonusu), reap miktarı,
  `@ResourceFound`/`@ResourceGather`, tükenmiş damar. **Bir boşluk:** `mr_nothing`
  çekilişi düğüme YAZILMIYORDU — referans boş çekilişte de düğümü kurar ve o
  tanımın REGEN'iyle ömürlendirir. Canlı pakette `mr_nothing` bir saatlik REGEN
  taşıyor ve suda %60 ağırlıkta; bizde balıkçı tek karede durup yeniden
  çekebiliyordu.

  **PLAN-404 bununla kapandı:** kısmi maliyet (İŞ-25), kalite (İŞ-26), kaynak
  tükenmesi/retry (İŞ-27); stroke, tool kırılması ve toplama miktarı ölçülüp
  doğru bulundu.

- [x] **İŞ-28 — Pet: takipçi sınırını kim bağlar** (PLAN-405 birinci dilim) — **KAPANDI**
  PLAN-405'in "takip slotları" ayağı. **Ölçülüp zaten doğru bulunanlar:** slot
  maliyeti (`FOLLOWERSLOTS`, chardef'ten, varsayılan 1), `CURFOLLOWER` canlı
  taramayla, `MAXFOLLOWER`, ahır/figürin geri alma noktalarında kontrol.
  **İki boşluk:** (1) `OF_PETSLOTS` hiç okunmuyordu — sistemi kapatmış bir
  shard'ı bile sınırlıyorduk, canlı shard da kapatmış; (2) GM azami sayıyı
  aşamıyordu. İki kontrol noktası tek kapıya (`Character.FollowerCapApplies`)
  bağlandı.

- [x] **İŞ-29 — Pet: görevden alınan satıcı neyi geri verir** (PLAN-405 ikinci dilim) — **KAPANDI**
  PLAN-405'in "sahiplik değişimi" ayağı. **Ölçülüp zaten doğru bulunanlar:**
  sahiplik devrinde eski sahibin ilişkilerinin (IPET/FRIEND/bond) temizlenmesi,
  bond'un sahipsiz pette düşürülmesi. **Bir boşluk:** oyuncu satıcısı serbest
  bırakıldığında kasası ve oyunculardan aldığı mallar onunla birlikte sahipsiz
  kalıyordu; referans ikisini de sahibin BANKASINA taşır ve dokunulmazlığı
  düşürür. Sanal SELL stoku bilinçli olarak geri verilmiyor (eşya basmak olurdu).

- [x] **İŞ-30 — Pet/ölüm/logout: PLAN-405'in kalanı** — **KAPANDI**
  PLAN-405'in son üç ayağı (park/geri alma, ölüm, logout/relogin). **Dört boşluk:**
  (1) hayalet kapıdan geçemiyordu; (2) berserk yaratık terk ediyordu (referansın
  adını koyduğu çağırma sömürüsü); (3) gemideki oyuncu çıkış yapınca tekne yola
  devam ediyordu; (4) ahır kapasitesi hem yanlış eğriyi kullanıyor hem de
  MAXPLAYERPETS'i yanlış karakterden okuyordu. **PLAN-405 kapandı.**

- [x] **İŞ-31 — Death/corpse/loot** (PLAN-406) — **KAPANDI**
  Çoğu ayak zaten doğruydu (imleçteki eşya, newbie/blessed, ceset-birleşme,
  çürüme, suç kapısı, kayıt). **Üç boşluk:** (1) ölüm büyü etkilerini
  bitirmiyordu; (2) cesedin ağırlık sınırı yoktu (referansın adını koyduğu
  sömürü); (3) ceset yağması tanık hattını hiç çalıştırmıyordu — muhafiz
  çağrılmıyor, kişisel gri oluşmuyor, `@SeeCrime` ateşlenmiyordu.
  **PLAN-406 kapandı.**

- [x] **İŞ-32 — Konut: oneksiz anahtarlar ve tasima sandigi** (PLAN-501 birinci dilim)
  **İki boşluk:** (1) konut anahtarları yalnızca-yazılırdı — `BASESTORAGE=4688`
  canlı eve ulaşıyor ama `<BASESTORAGE>` hiçbir şey döndürmüyordu; (2) `MOVINGCRATE`
  hiç yoktu — referans taşıma sandığını EVİN üzerinde tutar ve Scripts-X konut
  diyalogları onu okur. Redeed artık eldeki sandığı kullanıyor.

- [x] **İŞ-33 — Konut: izin anahtarları ve işaret olayları** (PLAN-501 ikinci dilim) — **KAPANDI**
  Referans multi anahtar tablosu bizimkiyle karşılaştırıldı: 18 anahtar eksikti.
  Paket ölçümüyle sıralanıp gerçekten kullanılanlar eklendi (`ISOWNER` 45 yer,
  `GETCOOWNERPOS` 32, `GETFRIENDPOS` 29, `GETSECUREDITEMS` 3…); kullanılmayanlar
  kayıtta bırakıldı. Ayrıca kilit/güvence işaret olayları (`ei_house_lockdown` /
  `ei_house_secure`) kondu — paketin ev boşaltma akışı tam olarak onlara soruyor.
  **PLAN-501 kapandı.**

- [x] **İŞ-34 — Ev yıkımı: elindekini bırakmak** (PLAN-502) — **KAPANDI**
  PLAN-502'nin fiilleri (`MOVEALLTOCRATE` vb.) iki pakette de **hiç**
  kullanılmıyor — İŞ-8 kuralı gereği yazılmadılar. İş, dalganın kabul
  ölçütüne ("silme/redeed sırasında içerik kaybolmaz, taşınan nesne iki parent
  altında görünmez") göre gerçek yollara gitti. **Üç boşluk:** (1) silinen ev
  kilitlediklerini bırakmıyordu — kilitli eşya **taşınamaz**, yani temelli
  sıkışıyordu; (2) taşıma sandığı evin altında gömülü kalıyordu; (3) boş sandık
  bankaya postalanıyordu.

- [x] **İŞ-35 — Tasarım commit'i: fiyatlanabilir ve reddedilebilir** (PLAN-503) — **KAPANDI**
  **İki boşluk:** (1) `@HouseDesignCommit` commit'ten **sonra** ve yanlış
  argümanlarla ateşleniyordu — referans paket ARGN1/ARGN2'den inşaatı
  fiyatlıyor, bizde bir revizyonu "eski karo sayısı" diye okuyup çöp üzerinden
  ücretlendiriyor ve `RETURN 1` reddi hiçbir işe yaramıyordu; (2) değişmemiş
  tasarım yine commit sayılıyordu. Harita yürüme geometrisi ve save/reload
  bağları ölçülüp zaten doğru bulundu.

- [x] **İŞ-36 — Gemi: neden durduğunu söylemek** (PLAN-504) — **KAPANDI**
  Gemi fiil/anahtar yüzeyi referansla diff'lendi: **eksik yok** (önceki dalgalar
  kapatmış). Davranış tarafında **bir boşluk:** duran gemi hiçbir şey
  söylemiyordu — iki dümen mesajı da kullanılmayan anahtarlar olarak duruyordu.
  `OF_MapBoundarySailing` tüketicisiz kaldı (gerekçe kayıtta).

- [x] **İŞ-37 — Lonca: kimi kabul eder** (PLAN-505) — **KAPANDI**
  PLAN-505'in istediği ayırım yapıldı: **çekirdek önce doğrulandı** (priv/fealty/
  title/abbrev/oy/savaş-ittifak: tamam), sonra menüler bakıldı. **Bir boşluk:**
  kabul kapısı hiç yoktu — NPC üye olabiliyor, bir oyuncu iki loncaya birden
  girebiliyordu. **Menüler yazılmadı** ve sebebi kayda geçti: referansın kendisi
  de o fiilleri artık uygulamıyor.

- [x] **İŞ-38 — Sohbet: gizlilik düğmeleri** (PLAN-506) — **KAPANDI**
  Party tarafı baştan sona ölçüldü ve **zaten doğru** bulundu (yetki kapısı,
  `@PartyRemove`/`@PartyLeave` vetoları, lider ayrılınca dağılma, tek kişiye
  düşünce dağılma, çevrimdışı üye). **Bir boşluk sohbette:** istemcinin yedi
  gizlilik eylemi (özel mesaj aç/kapa, ad göster/gizle, `/whois`) hiç
  işlenmiyordu. **Dalga 5 kapandı.**

- [x] **İŞ-39 — Büyü bazlı kapsama matrisi** (PLAN-601) — **KAPANDI**
  "Okul enum-only" toplu etiketi ölçümle **dört noktada** çürütülüp
  [büyü matrisiyle](BUYU_MATRISI_TR.md) değiştirildi. Canlı paket 168 büyü
  tanımlıyor: 138 çalışıyor, 30 reddediliyor. Matris `SpellCoverageGuardrailTests`
  ile sabitlendi, yani belge paketten sessizce ayrışamaz.

- [x] **İŞ-40 — AOS+ property matrisi ve era kapısı** (PLAN-602) — **KAPANDI**
  Referansın 139 component property'si, era etiketleriyle birlikte iki pakete
  karşı ölçüldü: **canlı shard 2 tanesini** (`NIGHTSIGHT`, `RANGE` — ikisi de
  PRET2A) atıyor, modern referans paket 59. Yani AOS property işinin bu shard'da
  tüketicisi yok. Planın uyardığı "tooltip'te var, etkisi tamam" yanılgısı bizde
  **ters yönde**: tooltip hiçbir component property'si yayınlamıyor.

- [x] **İŞ-41 — Paket matrisi ve bonded durumu** (PLAN-603) — **KAPANDI**
  Paket katmanı artık sınıf sayısıyla değil [opcode/alt-komut
  matrisiyle](PAKET_MATRISI_TR.md) ölçülüyor. Ölçümün bulduğu gerçek boşluk:
  `0xBF 0x19` referansta **dört** mesaj taşıyor, bizde yalnızca stat-kilidi yarısı
  vardı — bonded bir evcil hayvanın hayaleti kimseye bonded olarak
  bildirilmiyordu.

- [x] **İŞ-42 — TCP parçalanma/birleşme ve yeniden bağlanma** (PLAN-604) — **KAPANDI**
  Var olan login entegrasyon testi her paketi **tek parçada** veriyordu — bir
  soketin asla söz vermediği tek şey. Sebebi bulundu: `InjectReceived` sona
  değil **başa** ekliyordu, yani parcali besleme ifade edilemiyordu. Yardımcı
  düzeltildi; **çerçeveleyicinin kendisi zaten doğruymuş.**

- [x] **İŞ-43 — SAVESTATICS kapsamı ve iş paketi ayrımı** (PLAN-605) — **KAPANDI**
  Planın "metin statics export'unu tam harita çıktısı sayma" uyarısı ölçüldü:
  **referansın SAVESTATICS'i de metin script yedeği yazıyor**, yani ikisi de harita
  çıktısı değil. **Bir boşluk:** süzgecimiz kap içindeki eşyayı ve multi'yi de
  yazıyordu. `USEMAPDIFFS` yazılmadı — bu shard'da ne ini anahtarı ne diff
  dosyası var. **Dalga 6 kapandı.**

- [x] **İŞ-44 — Tekrarlanabilir yük profili** (PLAN-701 + PLAN-703) — **KAPANDI**
  `LoadProfile.Capture` planın adını verdiği bütün sayacları tek kayıtta alıyor
  (oyuncu/NPC/spawner, hareket, savaş, script geri çağrımı, kayıt sıklığı +
  tick p50/p95/p99, kayıt süresi, bellek, GC, kuyruk, açıklanamayan nesne).
  **PLAN-702 (soak koşuları) bu turda çalıştırılmadı** — planın kendisi de
  öyle diyor; bu iş onların tüketeceği ölçüm zeminini kuruyor.

- [x] **İŞ-45 — Çökme/yeniden başlatma tatbikatı** (PLAN-704) — **KAPANDI**
  Beş tatbikatın üçü (**bozuk/eksik shard, disk yazma hatası, son iyi kayda
  dönüş**) `BootFallbackTests` + `SaveTransactionalCommitTests` +
  `BinarySaveAtomicityTests` ile zaten kapsanmıştı. **Kapsanmayan:** kaydın
  **öldürülmesi** (başarısız olması değil) — `.tmp` enkazı diskte kalır ve
  sonraki açılışın onu yok sayması gerekir. Ayrıca uçtan uca yeniden başlatma
  tatbikatı eklendi.

- [x] **İŞ-46 — Release kabul paketi** (PLAN-705) — **KAPANDI**
  [Kabul paketi](RELEASE_KABUL_PAKETI_TR.md) altı başlığı topluyor ve **ikisinin
  koşulmadığını açıkça yazıyor** (gerçek istemci smoke, soak). 13 kayıtlı sapma
  ve açık kalem listeleniyor; `ReleaseAcceptanceGuardrailTests` sayıları
  kaynaklarına bağlıyor. **Dalga 4-7 kapandı.**

## Yapıldı

Bu bölüm yalnızca bu plandaki işlerin kapanışını listeler; bulgu ayrıntısı takip
planındadır.

- **İŞ-86 KAPANDI (B dalgası 3. aşama ön koşulu: spawner ölüm olayı)** —
  2026-09-14. Kapasitesine dayanan spawner timer'ını park ediyor; onu yeniden
  başlatabilecek tek şey, **kendi tick'inin** temizlik geçişinde listenin küçüldüğünü
  fark etmesiydi — yoklama. Kimsenin durmadığı sektörde sıradaki tick 3 dakikalık
  bakım taraması, yani uzaktaki spawn noktasının son yaratığını öldürmek orayı
  **dakikalarca boş** bırakıyordu. Üretilen davranış: ölümden sonra üye=1, timer=-1.
  Kayıp artık **olay**: silinen nesne `SPAWN_POINT_UUID` üzerinden spawn noktasına
  bildiriliyor, o da üyeyi çıkarıp zaman aşımını yeniden kuruyor (kaynak
  `CCSpawn::DelObj`). **Bileşenin `DelObj`'u zaten doğruydu — yalnızca ölüm için
  çağıranı yoktu.** Kapasitenin altındaki spawner'a dokunulmuyor (her ölümde saati
  sıfırlamak, çok avlanan noktayı yavaşlatırdı). Test:
  `SpawnerDeathNotificationTests` (5); sondajlar 3/3 kırmızı. Tam suite 3.887, üç
  koşu. *Bu, @Timer'ı sektör tick'inden çıkarmanın ön koşuluydu: park edilmiş
  spawner'ın son tarihi yok, yani hiçbir vade kuyruğunda durmaz.*
- **İŞ-85 KAPANDI (B dalgası 2. aşama: çürüme vade kuyruğunda)** — 2026-09-14.
  Çürüme kontrolü, süresi dolanları bulmak için **5 saniyede bir dünyadaki her yer
  eşyasını** yürüyordu: 300.000 eşyalık dünyada **11,4 ms p50 / 13,7 p95**, üstelik
  o koşuda **hiçbir şey bulmadan**. 5 sn'lik ritim bu yürümenin bedeliydi, bir
  gereklilik değil. Kurulu son tarihler artık vade sıralı kuyrukta; kontrol her tick
  koşuyor ve **0,000 ms** (aynı dünyada, vadesi gelen yokken). Kayıt İŞ-84'te
  kurduğum tek kapıda yapılıyor — ön koşul buydu. Girdiler yapıldıkları andaki son
  tarihi taşıyor: yeniden kurmak yeni girdi ekler, iptal hiçbir şey eklemez, bayat
  girdi yüzeye çıkınca düşürülür — kuyruğun hiçbir şeyi bulup çıkarması gerekmiyor.
  256 sınırı duruyor ama artık bir tick'in işini sınırlıyor; kalanlar 5 sn sonraki
  bir tam taramanın sonuna değil, bir sonraki tick'in başına gidiyor (birikim **50
  kat** hızlı eriyor). Uyuyan sektörde çürüme artık **kesin**.
  **Denetçi:** eski tam tarama mekanizma olarak değil, **dakikada bir** çalışan
  denetçi olarak duruyor — kuyruğun tutmadığı kurulu son tarihi yeniden kuyruğa alıp
  **logluyor**; kuyruk, onu besleyen kayıtlar kadar eksiksiz ve ulaşmamış bir son
  tarih hiç çürümezdi. 11 ms dakikada bir karşılanabilir, 5 sn'de bir karşılanamazdı.
  Test: `DecayDueQueueTests` (9); sondajlar 4/1/1 kırmızı. Tam suite 3.882, üç koşu.
  **Not:** İŞ-82'nin belge guardrail'i bu değişikliği ilk tam koşuda yakaladı (timer
  sözleşmesi hâlâ 5 sn'lik geçişi vaat ediyordu) — yazılma amacı buydu; ARCHITECTURE
  tablosu güncellendi.
  *Açık: @Timer ve spawn hâlâ sektör tick'inden yürüyor — spawner'ın ölüm olayıyla
  yeniden kurulması (Source-X `CCSpawn::DelObj`) gerektiği için ayrı dalga.*
- **İŞ-84 KAPANDI (B dalgasının ön koşulu: son-tarih tek kapı)** — 2026-09-14.
  `DecayTime` dışarıya açık bir alandı; motor + sunucu + testlerde ~34 doğrudan
  atama vardı. Artık dışarıdan salt okunur: `SetDecayTime` (süre, kaynağın
  `CItem::SetDecayTime` kuralları), `SetDecayAt` (mutlak son tarih), `ClearDecay`;
  Item içindeki her yazım tek bir `AssignDecay`'den geçiyor. `Timeout` zaten
  `ObjBase.SetTimeout`'tan geçiyordu — artık sabitlenmiş. **Neden:** timer'ları
  kaynağın tek zaman-sıralı due listesine (`CWorldTicker`) taşımanın ucuz yarısı;
  orada son tarih değiştiğinde **kaydedilmek** zorunda ve kaydetmeyi unutan tek
  atama, hiç çalışmayan bir timer üretir. Guardrail:
  `DeadlineWriteGateGuardrailTests` (3) — Item.cs dışında yazım yok, ObjBase.cs
  dışında yazım yok, kapılar yerinde. Sondaj: kaçak yazım **derlenmiyor bile**,
  setter'ı geri açmak 1, ikisi birden 2 kırmızı.
  **Asıl bulgu:** `MultiReader`, paralel yoldaki **her okumada** paylaşılan
  `FileStream`'den `SafeFileHandle` okuyordu — o özellik düz erişimci değil,
  stream'i flush edip handle'ı yeniden konumluyor. B4'teki konumsal okuma onarımı ve
  `Length` önbelleği bunu çözmemişti; flake iki kez daha tekrarladı. Okuyucu artık
  dosya handle'larını kendisi açıp tutuyor, hiçbir stream'e dokunmuyor (MapReader'ın
  baştan beri yaptığı gibi); kurucudaki bileşen-boyu tespiti de konumsal.
  **Kanıt:** onarımdan önce ~9 tam koşuda 2 kırmızı + 2 özetsiz test-host çöküşü;
  sonrasında **6 koşu, 6 temiz** (3.873). Tekrar üretilemeyen bir arıza olduğu için
  "kesin çözüldü" demiyorum — kaldırılan şey belgelenmiş bir thread-safety ihlali ve
  kanıt bu yönde.
- **İŞ-83 KAPANDI (B7'nin devamı: uyku politikası + yük ölçümü)** — 2026-09-14.
  `SECTORSLEEP`'in config anahtarı, varsayılanı, yordamı (`Sector.CanSleep`),
  bağlama satırı ve testleri vardı — **dünya tick'i hiç sormuyordu**; sektör yalnız
  5×5 pencere içindeyse uyanıktı. `config/sphere.ini` bunu yorumunda zaten yazıyordu
  ("Config'e yükleniyor ama sektör uyku mantığına bağlı değil"). Tick artık
  müsamahayı uyguluyor; damga oyuncunun **içinde** olduğu sektöre vuruluyor, yani iz
  bir sektör genişliğinde. Komşuluk kontrolü atlandı (bizim pencere ±2, kaynağınki
  ±1 — kapsanıyor). `SECTORSLEEP=0` kaynak anlamını koruyor + açılışta uyarı.
  `Sector.IsSleeping` hiç atanmıyordu; admin listesi her sektörü uyanık gösteriyordu.
  **Ölçüm (500 oyuncu / 50.000 NPC / 300.000 eşya, 6144×4096, 355 MB heap, yalnız
  dünya tick'i):** şehirlerde kümeli iz yokken 3,2/4,7 ms (p50/p95), sevk edilen 1 dk
  müsamahayla 3,5/5,1, kaynağın 10 dk'sıyla 5,5/7,9 — 100 ms bütçenin %8'i. Aynı 500
  oyuncu haritaya **eşit dağıldığında** 66,8/171,3 ms — bütçe dışı, **müsamahadan
  bağımsız** olarak: maliyet oyuncu sayısı değil **uyanık alan**. Probe:
  `SectorSleepLoadProbe` (`SPHERENET_LOADPROBE=1`, varsayılan kapalı).
  **Yan bulgu:** `MultiReader`, paralel yolda paylaşılan `FileStream`'e `Length`
  soruyordu (thread-safe değil) — tam suite'te bir kez ara sıra `IOException` olarak
  çıktı; B4 onarımının geride kalan yarısı, uzunluklar artık kurucuda bir kez
  okunuyor. Test: `SectorSleepGraceTests` (5). Tam suite 3.870, üç koşu.
  *Ölçüm yalnız dünya tick'ini kapsıyor: ağ döngüsü ve NPC AI (kendi tick bütçesi
  var) dahil değil.*
- **İŞ-82 KAPANDI (Beyond-Source-X B7)** — 2026-09-14. README "timer'lar duvar
  saati hassasiyetinde", ARCHITECTURE "kaymadan zamanında çalışır" diyordu. Son
  tarihin mutlak olması ile geri çağırmanın o an **çalışması** aynı şey değil:
  uyuyan sektördeki yer eşyasının `TIMER`'ı üç dakikalık bakım taramasını bekliyor.
  Önce **ölçtüm**, sonra yazdım: aktif sektör → sonraki tick; `TIMERF`, yerde
  olmayan eşya → her yerde sonraki tick; uyuyan sektör yer eşyası → 180 s tarama
  (tick başına 64 sektör); çürüme → 5 s'de 256 eşya (~51/sn); uyuyan sektördeki
  karakter → oyuncu gelene kadar **hiç**; `SECF_NoSleep` → aktif gibi.
  **Kod:** `CanFlags.O_NoSleep` tanımlıydı ve **hiçbir yerde okunmuyordu**
  (Source-X `_TickableStateOverride`); artık bayraklı yer eşyası dünya düzeyindeki
  kurulu-timer kaydından pompalanıyor — due listesi, tarama değil. **Belge:** tablo
  ARCHITECTURE'da, dört sayı guardrail ile motor sabitlerine iğnelendi.
  Test: `SleepingSectorTimerContractTests` (13); sondajlar 3/1/3/1 kırmızı, belge
  sayısını bozmak guardrail'i kırıyor. Geleceğe kurulan timer testi kendi
  uygulamamı kırmızıya düşürdü ve düzeltildi. Tam suite 3.864, üç koşu.
  *Pet/summon, teleport, sektör sınırı ve uyanış p99 ölçülmedi; karakter tarafı
  NOSLEEP uygulanmadı.*
- **İŞ-81 KAPANDI (Beyond-Source-X B6)** — 2026-09-14. Kaydedici teşhis aracı;
  arıza biçimi hızından önemli. Üç arıza: (1) `Dispose`, sonucuna bakmadığı bir
  `Join`'den sonra writer'ın bağlantısını kapatıyordu — uzun süren son flush işlem
  ortasında **kendi bağlantısının** altından çekildiğini görüyor, kayıtlar
  kayboluyor, shard temiz kapanış bildiriyordu; (2) kapanıştan sonra gelen tick,
  `Dispose`'un hiç temizlemediği `_db` null kontrolünü geçip dispose edilmiş
  handle'da `Set()` çağırıyordu — sunucunun tick'inden `ObjectDisposedException`;
  (3) "500.000 sınırı" yalnız flush `catch`'indeydi ve batch'in tamamını geri
  ekliyordu, üretici tarafı hiç bakmıyordu — bir sınır değil, ipucuydu. **Onarım:**
  önce üretici durur, writer beklenir, sonra kaynaklar kapatılır; writer hâlâ
  çalışıyorsa bağlantı açık bırakılır ve aşım yazılamayan kayıt sayısıyla loglanır.
  Kapasite **girişte** (üretici + geri-ekleme), aşan kayıt `dropped` sayılır;
  backlog/en eski yaş/written/dropped/retried/flush-hatası sayaçları + dakikada bir
  uyarı. `_lastPositions` artık kadro anlık görüntüsüne göre temizleniyor. Test:
  `StateRecorderShutdownTests` (5), **gerçek SQLite yazma kilidi** dahil; dört
  sondajın her biri bir kırmızı. Tam suite 3.851, üç koşu. *Disk doluluğu/yavaş disk
  senaryoları ayrı koşulmadı; spool yazılmadı — politika açık kayıp + rapor.*
- **İŞ-80 KAPANDI (Beyond-Source-X B5)** — 2026-09-14. Tick başına A* bütçesi,
  paralel işçilerin kapıştığı bir sayaçtı: aramayı **ilk ulaşan** kazanıyordu ve
  kaybeden 150 ms yol ertelemesi alıyordu — ki bu **durum**. Kararları uygulamadan
  önce sıralamak, farklı bir **kümenin** seçilmiş olmasını geri alamaz; yani
  "deterministik" uygulama sırasını anlatıyordu, ortaya çıkan dünyayı değil.
  **Onarım (planın kendi tarifi):** adaylar artık **seri aşamada**, kararlı ve
  **dönen** sırayla seçiliyor — sabit sıra deterministik olur ama kuyruğu sessizce aç
  bırakırdı. Kabul kota değil **tavan**. Kurulmamış bütçe yine **sınırsız**: ilk
  denememde "kimse arama yapamaz" oldu ve mevcut bir test yakaladı — tam da bu
  incelemenin peşindeki sessiz arıza türü olurdu. Test:
  `PathBudgetDeterminismTests` (5); sondajda adalet 1, kabul 4 kırmızı. Tam suite
  3.846, üç koşu. *Planın "tick zamanını tüm Build koduna geçir" maddesi açık.*
- **İŞ-79 KAPANDI (Beyond-Source-X B12)** — 2026-09-14. Multicore tick,
  `ViewNeedsRefresh`'i `ApplyViewDelta`'dan **önce** temizliyordu; Apply veya
  statik-kapı senkronu patlarsa tick tükettiği NPC'leri kurtarıyor ama **o
  istemcinin** tazeleme isteğini kimse yeniden kurmuyordu — oyuncu hareket edene
  kadar ekranındaki eksik eşya/yaratık eksik kalıyordu. Sessiz arıza: sunucu
  toparlanıp tam hızda devam ediyor, yanlış dünyayla kalan tek kişi oyuncu. İstek
  artık yalnız görünüm **gönderildikten sonra** tüketiliyor; exception yutulmadan
  yeniden fırlatılıyor ki tick'i bırakma kararı tick'in kendi işleyicisinde kalsın.
  Tek-thread'li iki yol kontrol edildi — zaten doğruydu. Test:
  `ViewRefreshRecoveryTests` (4); sondajda 1 kırmızı. Tam suite 3.841, üç koşu.
  *D03'ün geniş multicore hata-enjeksiyon matrisi açık.*
- **İŞ-78 KAPANDI (Beyond-Source-X B10 + B11'in kısa-okuma yarısı)** — 2026-09-14.
  **B10:** replay'in *"serial'lar nerede"* tablosu yanlıştı, ve orada yanılmak
  atlamak değil **bozmak**: ses paketinde serial yokken mode/ses/seviye eziliyordu
  (`540101230000…` → `543FFF000100…`), efektte tür karalanıp **hedef canlı
  kalıyordu**, container item'da yığın offset'i/miktar/koordinat eziliyor ve iki
  serial de yerinde duruyordu. Her girdi artık paketi üreten **writer'a karşı**
  doğrulandı, serial taşımayan paket boş girdi, `0x25` uzunluğunun söylediği düzeni
  alıyor. **B11:** `ReadBytes` elindekini döndüğü için kesik `.rec` dosyası, son
  paketi eksik bir oturum üretip **bozuk paketi gerçek istemciye** gönderiyordu.
  Test: `ReplayPacketIntegrityTests` (8) — paket saymak yerine gerçek writer
  çıktısını çözüyor; sondajda 2 ve 1 kırmızı. Tam suite 3.837, üç koşu. *İkisi de
  yarım: B10'un tam opcode envanteri ve B11'in boyut/sıralama/atomik-yazım
  doğrulamaları açık.*
- **İŞ-77 KAPANDI (Beyond-Source-X B8 + B9'un kapat/aç yarısı)** — 2026-09-14.
  **B9:** `UseThread` oturumunda `Close`, kuyruk üzerinde kalıcı `CompleteAdding`
  çağırıyor ve alan readonly olduğu için sonraki `Connect` **bir daha iş kabul
  etmeyecek** kuyrukta taze işçi başlatıyordu — bağlan/sorgu/kapat/**yeniden bağlan
  hepsi başarılı bildiriyor**, yalnız sonraki sorgu patlıyordu; bir script'in bunu
  veritabanı sorunundan ayırt etmesi imkânsız. Yeniden açılış artık yeni kuyruk
  üretiyor. **B8:** varsayılan provider `MySqlConnector` hiçbir yerde kayıtlı
  değildi (paket referansı da yoktu) — varsayılanları kullanan shard ağda değil
  **provider çözümlemesinde** kalıyordu. Paket eklendi, factory kaydedildi; test
  sunucunun **kendi** kayıt yolundan doğruluyor. Test: `DbSessionLifecycleTests` (6);
  sondajda 3 ve 1 kırmızı. Tam suite 3.829, üç koşu. *B9'un timeout yarısı (bekleme
  nesnesi ömrü, sınırsız kuyruk, başarısız sorgudan sonra duran ROW) ve D02'nin
  gerçek MySQL matrisi açık.*
- **İŞ-76 KAPANDI (Beyond-Source-X B4)** — 2026-09-14. Klasik `.mul` zemin bloğu
  okuması **tek ve paylaşılan** bir `BinaryReader` üzerinde seek + 193 küçük okuma
  demekti; sekiz işçi, 20.000 okuma → **10.104 yanlış blok, 3.403 exception**
  (incelemenin koşusu 12.197/3.506). Paralel NPC prestage zemini çözdüğü için bir
  yaratık, yüksekliği dünyanın başka yerinden gelmiş bir karonun üzerinden yol
  bulabiliyordu. **Onarım:** kilit değil, **konum tabanlı okuma** (`RandomAccess`) —
  kilit her harita okumasını sıraya dizerdi. **Komşular kontrol edildi:** statics ve
  UOP güvenli; **`MultiReader` aynı kusurdaydı** (önbelleklediği için yalnız ilk
  görülmede, ki o da gemiye/eve ilk adım anı) ve önbelleği düz `Dictionary`'ydi —
  ikisi de düzeltildi. Test: `MapReaderConcurrencyTests` (6); sondajda 4 ve 1
  kırmızı. Tam suite 3.823, üç koşu. *Gerçek MUL üzerinde prestage entegrasyon
  testi ve MUL/UOP karşılaştırması yapılmadı.*
- **İŞ-75 KAPANDI (Beyond-Source-X B3)** — 2026-09-14. İnceleme bunu kod bulgusu
  olarak işaretleyip bariyerli yarış testi istemişti; test yazıldı ve yarışı
  **deterministik** gösterdi: tek bir Text dosyası olarak hazırlanan nesil, Text bir
  `spheredata`'nın yanında **dört Binary shard** olarak indi — yani değişiklik kaydı
  çevirmiyor, **bölüyor**. Arka plan modunda gezinti ana döngüde, yazma işçi
  thread'de ve ikisi de aynı değiştirilebilir saver'dan okuyordu. **Onarım:** yazma
  aşaması artık kaydın **hazırlandığı andaki** ayarları kullanıyor; devam eden yazma
  başladığı şey olarak bitiyor, yeni format sonraki kayıtta geçerli. Ayrıca
  `SAVEFORMAT` artık koşulsuz "ve şimdi kaydediyorum" demiyor. Test:
  `BackgroundSaveFormatRaceTests` (3); sondajda 2 kırmızı. Tam suite 3.817, üç koşu.
  **İncelemenin üç P1 kayıt bulgusu (B1/B2/B3) da kapandı**; nesil bazlı yayınlama
  tasarımı ve D01 matrisi açık.
- **İŞ-74 KAPANDI (Beyond-Source-X B1)** — 2026-09-14. Yeniden üretim incelemenin
  dosya listesini aynen verdi. **İki ayrı kusur:** (1) `SAVEFORMAT` değişince dünya
  ad değiştiriyor, yedekler **dosya adına göre** döndüğü için eski dosyaları kimse
  döndürmedi ve bayat-kardeş temizliği onları sildi — geri dönüş yolu kalmadı;
  (2) yükleyici geriye kalan **yalnız sunucu verisini** dünya sayıyor, (0, 0)
  dönüyor ve sonraki kayıt o boşluğu kalan yedeklerin üzerine yazıyordu. **Onarım:**
  yerine geçilen dosya kendi uzantısının yedek zincirine **emekli ediliyor**
  (`BackupLevels=0` iken değil); yükleyici dünya/karakter dosyası olmayan nesli
  reddedip önceki nesle düşüyor. Artık yeni dünya dosyası kaybolan bir geçiş önceki
  dünyasıyla geri geliyor. Test: `SaveGenerationIntegrityTests` (11); sondajda 2/2
  kırmızı. Tam suite 3.814, üç koşu. *Nesil bazlı yayınlama tasarımı ve B1'in tam
  kabul matrisi hâlâ yapılmadı.* Sıradaki: B3.
- **İŞ-73 KAPANDI (Beyond-Source-X B2)** — 2026-09-14. Ana planda Dalga 3 kapandığı
  ve PLAN-702 (soak) bu turda koşulmayacağı için inceleme planının P1 kuyruğuna
  geçildi. **Sorun:** kayıt nesli tutarlılığı her tabanın yalnız **ilk** dosyasına
  bakıyordu; shard'lı kayıtta ikinci shard hiçbir şeyle karşılaştırılmıyor, eski bir
  kopyası konduğunda dünya **iki farklı andan** kuruluyordu. Ayrıca ilk **damgasız**
  dosya doğrulamayı tümden kapatıyordu. **Onarım iki parçalı:** (1) her dosya
  karşılaştırılıyor, damgasız olan yalnız kendisi atlanıyor; (2) bu yetmedi — sayaç
  yeni süreçte sıfırlandığı için iki ilgisiz kayıt aynı numarayı taşıyordu, bu yüzden
  her kayıt artık **benzersiz bir nesil jetonu** yazıyor (`GEN=` / `SAVEGENERATION=`),
  jeton yoksa eski sayaca düşülüyor. Klasik damgasız kayıt kabul edilmeye devam
  ediyor. Test: `SaveGenerationIntegrityTests` (6); sondajda her iki parça için de
  4'er kırmızı. Tam suite 3.809, üç koşu. Sıradaki: B1 / B3.
- **İŞ-72 KAPANDI (PLAN-302 dördüncü paketi — PLAN-302 TAMAMLANDI)** — 2026-09-14.
  **Sessiz ve tam bir kayıp:** paketteki her `[SKILLCLASS n]` yükleniyor ve bir daha
  **bulunamıyordu** — bölüm sayı yerine defname gibi hash'leniyordu. Canlı paketin
  kendi 0 sınıfı (`STATSUM 300`, `SKILLSUM 10000`, `STR/INT/DEX 100`, tam yetenek
  tavanı tablosu) ayrıştırılıp yok sayılıyor, her oyuncu motorun yedek tavanlarını
  alıyordu. **Yeni:** `REVEALFLAGS` (on bir bayrak, **üçü ters** anlamda; konuşma ve
  hırsızlık sonuçları daha önce hiç açığa çıkarmıyordu), `IniParser.GetFlags`
  (`01|02|010` biçimi — `GetInt` bunu sessizce yanlış okurdu), `OVERSKILLMULTIPLY`
  (SKILLCLASS'ın STR/DEX/INT tavanlarına ilk tüketici), `HITSHUNGERLOSS`
  (ters yöne taşmış bir yorum da düzeltildi), `SKILLPRACTICEMAX`, `WOOLGROWTHTIME`.
  **Ayrıca** `DelayedCallSaveDuringCallbackTests`'in saat yarışı düzeltildi (yarı
  yarıya, hiçbir şey ölçmeyerek kalıyordu). Test:
  `RevealAndStatRepairConfigTests` (13); sondajda 3/5/1/1/2 kırmızı. Tanınan ini
  anahtarı 178 → **183**. Tam suite 3.803, üç koşu. **Dalga 3 (PLAN-301..305) kapandı.**
- **İŞ-71 KAPANDI (PLAN-302 üçüncü paketi)** — 2026-09-14. **İki gerçek hata:**
  (1) başkasının üzerinden eşya alma kapısı "başka bir OYUNCU değilse" diye
  yazıldığı için **dünyadaki her NPC herkese açıktı** — yabancının ejderhası,
  kiralık asker, dükkâncı. Referansın kuralı sahiplik üzerine
  (`CCharAct.cpp:2946`); ayrıca kuralın **çelişen iki kapısı** vardı, tek
  `CanTakeFrom` oldu. (2) `CanShove` yalnız staminaya bakıyordu ve boştaki NPC'nin
  staminası hep dolu → **yaratıklar birbirinin içinden geçiyordu**. **Altı anahtar:**
  `CANUNDRESSPETS`, `CANPETSDRINKPOTION` (iksir okuyucusu içme yoluyla paylaşıldı),
  `VENDORMARKUP` (tüketicisi vardı, **besleyicisi yoktu**), `VENDORMAXSELL` (liste
  kurulurken), `LOSTNPCTELEPORT`, `NPCSHOVENPC`. Test: `PetVendorConfigTests` (13);
  sondajda 2/1/1/1/1 kırmızı. Tanınan ini anahtarı 172 → **178**. Tam suite 3.790,
  üç koşu. **PLAN-302 hâlâ açık** — dördün üçü bitti; sıradaki suç/stat ve çevre.
- **İŞ-70 KAPANDI (PLAN-302 ikinci paketi)** — 2026-09-14. **İki davranış
  düzeltmesi:** (1) meditasyon yapan karakter adım atamıyordu; referans buna
  **izin veriyor**, yalnız `MEDITATIONMOVEMENTABORT` kuruluyken kesiyor ve
  varsayılanı kapalı — motor koşulsuz iptal ediyordu. (2) Dirilme cüppesi kefen
  bayrağına bağlıydı; görünmez hayalet isteyen shard insanları çıplak diriltiyordu.
  **Uydurma değer:** kilit açma zorluğu sabit `Random.Next(60)` idi (asma kilit =
  kasa kapısı); artık kilidin kendi karmaşıklığı, ve **anahtar çantadaysa önemsiz**.
  **Yeni:** `MAGICUNLOCKDOOR` (yetenekten önce atılan N'de-bir şans; referans ini
  onu yanlış anlatıyor), `SPELLTIMEOUT` (hedef imlecine son tarih; karakterin kendi
  tag'i ezer), `NORESROBE`. **Tekilleştirme:** anahtar-kilit eşleşmesinin iki
  kopyası tek `Item.KeyFits` + `Character.FindKeyFor` oldu. Test:
  `MagicInterruptConfigTests` (10) + `SpellTargetTimeoutTests` (5); sondajda 1/2/1
  kırmızı. Tanınan ini anahtarı 168 → **172**. Tam suite 3.777, altı koşu.
  **PLAN-302 hâlâ açık** — dört paketin ikisi bitti; sıradaki pet/vendor.
- **İŞ-69 KAPANDI (PLAN-302 ilk paketi)** — 2026-09-13. **Gerçek düzeltme:**
  sunucu `BACKPACKOVERLOAD=40` gönderiyordu ama **bedelini hiç ödetmiyordu**;
  koddaki yorum yürümenin stamina yakmadığını *iddia ediyordu*, oysa ücret
  `Event_Walk`'ta değil bir kat aşağıda (`CCharAct.cpp:4787-4829`). **Altı yeni
  anahtar** davranışa bağlandı: `STAMINALOSSATWEIGHT` (eşik değil S-eğrisi orta
  noktası), `STAMINALOSSOVERWEIGHT` (limit üstü her adım, +1/5 taş, atlıda ÷3),
  `RUNNINGPENALTY`, `RUNNINGPENALTYOVERWEIGHT` (bilinçli sapma: referansın tablosu
  bu anahtarı komşusunun alanına bağlamış), `DRAGWEIGHTMAX` (kendi çantandakiler
  ayağına düşer), `MOVERATE`. **İni belgesi:** uydurma `MONSTERTIGHT` kaldırıldı,
  `DISTANCEFORMULA`'nın değerleri ve varsayılanı düzeltildi, `BACKPACKOVERLOAD`
  başlığındaki çelişki temizlendi. Test: `WeightMovementConfigTests` (10) +
  `DragWeightAndMoveRateTests` (8); sondajda 8/2/1 kırmızı. Tanınan ini anahtarı
  162 → **168**. Tam suite 3.762, üç koşu. **PLAN-302 açık kalıyor** — dört
  paketin biri bitti; sıradaki büyü/interrupt paketi.
- **İŞ-68 KAPANDI** — 2026-09-13. **İki gerçek düzeltme:** kaynak listesi
  dilbilgisinin (`CResourceQty.cpp:57` — *"either order"*, yalın ad = 1) bizde
  **dört yarım okuyucusu** vardı. Canlı pakette bedeli: yazıcılık **kalem ve
  mürekkepsiz** yapılabiliyordu (`1 i_pen_and_ink` "skill 1" sanılıyordu) ve yalın
  adla yazılmış **257 `RESOURCES` satırı** düşürülüp malzeme bedava veriliyordu.
  Ayrıca yalın tamsayı skill değeri on katına çıkarılıyordu (76 → 76.0, referans
  7.6). Tek okuyucu: `ResourceQtyList`. **Yeni:** `SKILLCHECK` (zar atar, deneyim
  vermez), `SKILLADJUSTED` (metin döner: "52.0"), `SKILLTEST` (skill + eşya tek
  soruda, sahiplik üretim motorunun stok aramasından). Test:
  `ResourceListGrammarTests` (8) + `SkillQueryParityTests` (11); sondajda sırasıyla
  6/2/5 kırmızı. Rapor listesi 43 addan 25 cevaplı (kalan 18). Tam suite 3.744, üç
  koşu. **PLAN-304 tamamlandı.** Sıradaki: PLAN-302 (seksen ini anahtarının
  paket paket tanımı) tek açık madde olarak kaldı.
- **İŞ-67 KAPANDI** — 2026-09-13. **Gerçek düzeltme:** kapı doğru sesleri
  çalıyordu ama iki sayıyı koda gömerek; `DOOROPENSOUND`/`DOORCLOSESOUND` sessizce
  ölüydü — PLAN-305'in istediği "property var / ses çalınıyor" ayrımının tam örneği.
  Her yön artık kendi anahtarından ayrı çözülüyor (`CItem.cpp:4655-4665`).
  **Yeni özellik:** `PICKUPSOUND` (yedek `SOUND_USE_CLOTH` 0x057), yalnızca
  kaldırana gönderiliyor — referans `addSound` ile o istemciye veriyor. Test:
  `ItemSoundPropertyTests` (7); sondajda kapı araması sabitlenince 2'si kırmızı.
  Rapor listesi 43 addan 22 cevaplı (kalan 21). Tam suite 3.725, üç koşu.
  **PLAN-305 tamamlandı.** Sıradaki: PLAN-304 (CANMAKE/SKILL* sorgu sözleşmesi).
- **İŞ-66 KAPANDI** — 2026-09-13. **Yeni özellik:** `MODMAXHITS`/`MODMAXMANA`/
  `MODMAXSTAM`. Bir stat tavanı referansta **üç** terimli, bizde ikisi vardı:
  `Stat_GetMaxAdjusted = Stat_GetMax + Stat_GetMaxMod` (CCharStat.cpp:301).
  Aile artık **temel + değiştirici + takım** ve üçü bilerek ayrı: temele katılan
  bir değiştirici temel olarak persist edilir ve her kayıt turu/kopyalama onu
  tekrar ekler — 13J'nin takım teriminde bulduğu katlanma. Değer **işaretli**
  (`GetArgSVal`), yazınca mevcut değeri **yalnızca aşağı yönde** kırpıyor
  (yükselen tavan iyileştirme değil), kendi anahtarıyla persist ediliyor
  (CChar.cpp:4262) ve kopyaya **değiştirici olarak** geçiyor. Test:
  `ModMaxStatParityTests` (8); üçüncü terim kaldırılınca **8'de 6 kırmızı**.
  Rapordaki "canını yakacak eksikler" listesinden üç ad düştü (43'ün 19'u
  cevaplı, kalan 24). Tam suite 3.717, üç koşu. **PLAN-303 tamamlandı.**
  *Not: PLAN-302 (seksen anahtarlık paket tanımı) açık bırakıldı; PLAN-303
  somut bir uygulama olduğu için öne alındı.*
- **İŞ-65 KAPANDI** — 2026-09-13. **Gerçek düzeltme:** desteklenmeyen bir ini
  anahtarı ile **yanlış yazılmış** bir anahtar dışarıdan aynı görünüyordu — satır
  dosyada, sunucu açılıyor, hiçbir şey olmuyor; ayrıştırıcı iki durumda da
  susuyordu. `IniParser` artık sorulan her anahtarı işaretliyor (her okuma
  `GetValue`'dan geçtiği için tek nokta yetiyor) ve başlangıçta okunmayanlar
  yazdırılıyor. Ayrı bir "desteklenen anahtarlar" listesi **bilerek reddedildi**:
  okuyucudan sapar ve sapmış rapor güven veren yönde yanlış olur. Host/Panel aynı
  dosyayı kendi ayrıştırıcılarıyla okuduğu için 9 anahtarı **adıyla
  kredilendirildi** — çalışan ayarı "hiçbir şey yapmıyor" diye bildirmek İŞ-47'de
  düzeltilen kusurun tersi olurdu. Depo config'inde geriye **16** anahtar kalıyor
  ve hepsi dosyada `[UYGULANMADI]` işaretli. Test: `IniUnreadKeyReportTests` (5);
  işaretleme kapatılınca beşi de kırmızı. Tam suite 3.709, üç koşu.
  **PLAN-301 tamamlandı.**
- **İŞ-64 KAPANDI** — 2026-09-13. PLAN-206'nın yöntemi: eksik trigger'ları **enum
  adı üzerinden değil** referansın ad/alias/**bağlam** eslemesiyle çıkar. Ham ad
  karşılaştırması **71**; her adı sahip tablosuyla taşıyınca **21**. Farkların
  neredeyse hepsi yazım/bağlamdı: `SELECT` hem `CSkillDef`'in hem `CSpellDef`'in
  (biz `SkillSelect`/`SpellSelect`), referans bileşik adları alt çizgiyle ayırıyor
  (`@DropOn_Char`), char tablosu item tablosunu `ITEM` önekiyle aynalıyor,
  bölgenin `EXIT`'i `RegionLeave`, `AAAUNUSED` dolgu. Kalan 21, port raporunun
  **elle vardığı listeyi bağımsız olarak yeniden üretiyor.** Rapordaki "~27" → 21
  ve artık bunun bir **ad** sayımı olduğunu söylüyor; neyin ateşlendiğinin
  otoritesi `TriggerCoverageGuardrailTests`. Test: `TriggerNameMappingTests` (3);
  bir tablo kuralını kaybedince test kırılıyor. Tam suite 3.704, üç koşu.
  **PLAN-206 tamamlandı — Dalga 2 bitti.**
- **İŞ-63 KAPANDI** — 2026-09-13. PLAN-205'in beş TIMERF durumundan dördü zaten
  kapsanmıştı; beşinci — **iş listesi gezilirken dünyayı kaydeden callback** —
  sessiz bozulma biçimini taşıyordu ve testi yoktu. Vadesi gelen tüm işleri önden
  çekmek makul görünür ama script callback içinden kayıt alınca koşmamış işler
  dosyaya girmez ve yeniden başlatma planlanmış işi kaybeder. Üç vaka: ilk
  callback'te alınan kayıt koşmamış ikisini yazıyor; o anda koşan iş
  **yazılmıyor** (yeniden başlatma işi tekrarlamasın); callback'in planladığı yeni
  iş aynı dosyaya giriyor. **Üretim değişikliği yok** — önden boşaltmaya çevirince
  ilk vaka kırmızı. Test: `DelayedCallSaveDuringCallbackTests` (3).
  Tam suite 3.701, üç koşu. **PLAN-205 tamamlandı.**
- **İŞ-62 KAPANDI** — 2026-09-13. **Gerçek hata:** silinen nesnenin uid'i tahsis
  ediciye **anında** geri veriliyordu, bir sonraki oluşturma onu alıyordu — silmeden
  önce yakalanmış `NEW`/`ACT` **başka bir nesneye** çözülüyor, `NEW.NAME` bir
  yabancıyı düzenliyordu. Ölçüm: `stale=040000001 second=040000001 resolves to
  'somebody else'`. Referans silme anında asla geri dönüştürmez; serbest listesini
  **çöp toplamada** yeniden kurar (CWorld.cpp:655) ve `NEW`/`OBJ` okuması `ObjFind`
  ile doğrulayıp temizler (CScriptObj.cpp:617-626) — bu ancak yuva boşsa işe yarar.
  Düzeltme: serbest uid'ler bekleyen kuyrukta durup **bakım sweep'i tamamlanınca**
  veriliyor; geri dönüşüm sürüyor (dizin 28 bit, gecikmenin maliyeti yok).
  Test: `ScriptReferenceContractTests` (6). **Dört mevcut test** geri dönüşmüş uid
  durumunu kurmak için anında geri dönüşüme dayanıyordu; gerçek garantileri
  koruduğu için **zayıflatılmadı**, ortak yardımcıyla sweep'i sürer hale getirildi.
  Sondaj: anında geri dönüşüme dönülmesi iki testi kırmızıya çeviriyor.
  Tam suite 3.698, üç koşu. **PLAN-204 tamamlandı.**
- **İŞ-61 KAPANDI** — 2026-09-13. PLAN-203'ün istediği **matris**: @PreSpawn →
  oluşturma → @Spawn → yerleştirme → üyelik → @AddObj dizisi, zincirin tamamı için
  tek vaka yerine her sözleşme için bir vaka. **İki veto aynı veto değil:**
  @PreSpawn nesne oluşturulmadan döner (hiçbir şey yaratılmaz), @Spawn'a ise var
  olan nesne verilir — vetosu onu **silmek zorunda** (CCSpawn.cpp:422), yoksa
  dünyaya sahipsiz yaratık sızar. Ayrıca: script'in @Spawn'da seçtiği nokta
  yerleştirmeden sağ çıkıyor (:428, kontrol testiyle), @Spawn/@AddObj aynı
  nesneyi alıyor, üst düzeyde olmayan spawner hiç callback koşmuyor (:383).
  **Üretim değişikliği yok** — dizi zaten uyuyordu; @Spawn vetosundan silme
  kaldırılınca yedinin biri kırmızı. Test: `SpawnCallbackMatrixTests` (7).
  **Üç fikstür hatası kayda geçti** (hedefsiz spawner, kurucuda yüklenen tanımlar,
  tek başına `ForceSpawn`) — her biri iyi görünüp hiçbir şey ölçmeyen test
  üretti. Tam suite 3.692, üç koşu. **PLAN-203 tamamlandı.**
- **İŞ-60 KAPANDI** — 2026-09-13. **Gerçek hata:** TEMPLATE tarifi **iki ayrı
  okuyucuyla** yürütülüyordu. `TemplateEngine` `Rows`'u (her satır, sırasıyla),
  NPC loot açılımı ise `ItemEntries`'i (yalnızca ITEM/CONTAINER) geziyordu —
  yani tarifin taşıdığı **her property satırını sessizce düşürüyordu.** Aynı
  tarif NEWITEM/spawner'dan `'Gilded Reward' hue=0x0489`, loot'tan
  `'Reward' hue=0x0000` üretiyordu. Referansın tek okuyucusu var
  (ReadTemplate, CItem.cpp:586/686). Loot yolu artık `BuildTemplate` çağırıyor
  (zaten hedef kap alıyordu); yerine kullandığı 42 satırlık kurucu **ölü kod
  bırakılmadı, silindi.** Test: `TemplateEntryPointParityTests` (3); ortak
  okuyucu devre dışı bırakılınca üçün ikisi kırmızı. Tam suite 3.685, üç koşu.
  **PLAN-202 tamamlandı.**
- **İŞ-59 KAPANDI** — 2026-09-13. **Gerçek hata:** giyimli bir karakteri
  kopyalamak `NEW`'i **gömleğine** bırakıyordu. `Character.CreateDupe` giyili her
  katmanı kopyalıyor, her kopya `NEW`'i bir eşyaya taşıyor ve karakter onu geri
  almıyordu; `DUPE` sonrası `NEW.NAME` yazan script kopyalanmış gömleği yeniden
  adlandırıyordu. Referans tam bunu onarıyor ve kendi yorumunda söylüyor
  (CChar.cpp:1274-1275). **İki ölçüm hatası benimdi:** karakter `DUPE` fiili
  çağıranın `ACT`'ini kurmuyor — asimetri referansın kendisinin (eşya fiili
  `CreateDupeItem` üzerinden kurar, CHV_DUPE kurmaz; `ACT`'i yalnızca NEWDUPE
  kurar); artık **olumsuz** olarak sabit. Test: `DupeEntryPointParityTests` (4);
  onarım kaldırılınca `NEW` yine eşyayı gösteriyor. Kayıtlı sapma: referans
  `fSetNew`'i parametre yapar (varsayılan false), biz `NEW`'i her oluşturmada
  oynatıp her genel girişte geri koyarız. Tam suite 3.682, üç koşu.
  **PLAN-201 tamamlandı.**
- **İŞ-58 KAPANDI** — 2026-09-13. Tur testi iki kaydı karşılaştırıp aynı olmalarını
  istiyordu; bu **sapmayı** yakalar, **eksikliği** yakalamaz — hiç yazılmayan bir
  alan iki özdeş dosya ve yeşil test üretir. Kaydediciden spawn üye yazımını
  silmek o karşılaştırmayı hiç rahatsız etmiyor. PLAN-107'nin istediği **alan
  bazlı** karşılaştırma eklendi: altı alan (temel stat, miktar, owner/parent,
  spawn üyeliği, timer, dinamik vendor içeriği) turun öbür ucunda **değer**
  olarak iddia ediliyor; aynı sondaj yeni testi kırmızıya döndürüyor. Fikstürde
  spawn üyeliği ve vendor içeriği hiç yoktu. **İki fikstür hatası önce motor
  hatası gibi göründü:** elle beyinsiz kurulan spawn üyesi `NPC=0→8` sapması
  bildirdi — oysa spawn yolu da beyinsize `Monster` veriyor, tur onarımı sapma
  sanmıştı; LAYER 26'ya konan vendor malı kayboldu — doğru olarak, o kap sanal,
  dinamik içerik layer 27'de. Yeniden yükleme artık sunucunun sırasını aynalıyor
  (`Program.WorldBootstrap.cs:177`). Tam suite 3.678, üç koşu.
  **PLAN-107 tamamlandı — Dalga 1 bitti.**
- **İŞ-57 KAPANDI** — 2026-09-13. Gerçek 56T kaydında (76.359 eşya / 4.187
  karakter) eşlenmeyen anahtar sayısı **sıfır** — ama bunu sabitleyen bir şey
  yoktu: test yalnızca iki skill adının parklanmadığını iddia ediyordu, yirmi
  başka anahtar birikse yine geçerdi. `Assert.Empty(unhandled)` eklendi.
  **Ölçümü iki kez yanlış yaptım ve ikisi de belgeye geçti:** (1) kaynak literal
  taraması paketteki her skill'i eksik gösteriyor, (2) sunucunun çözücüleri
  kurulmadan yükleme 76.359 eşyayı `BaseId=0` bırakıp 9 anahtar bildiriyor —
  bunları motor hatası diye raporlamak üzereydim. Belge:
  `docs/56T_ESLENMEYEN_ANAHTARLAR_TR.md`. Sondaj: `ResolveItemDefFullIndex`
  kapatılınca lonca taşının 4 anahtarı parklanıyor, sabitleme kırmızı.
  Tam suite 3.677, üç koşu. **PLAN-106 tamamlandı.**
- **İŞ-56 KAPANDI** — 2026-09-13. **İki sapma daha bulundu ve düzeltildi.**
  Referans "durduruldu"yu bayrak olarak tutmaz: STOP = `KillChildren` +
  `SetTimeout(-1)` (CCSpawn.cpp:1260-1264), kota dolunca da aynı şekilde parklar
  (:643); `DupeCopy` saatte ne varsa taşır (CItem.cpp:4109) ve `CCSpawn::Copy`
  timer'a hiç dokunmaz — yani geri sayım da durdurulmuş durum da kopyaya **o tek
  satırdan** geçer. Bizde (1) bileşen ilklendirmesi timer'ı yeniden kuruyordu,
  (2) ayrı tutulan "durduruldu" bayrağı kopyaya yalnızca kaydın yazdığı
  `SPAWNSTOPPED` tag'inden ulaşıyordu — **aynı oturumda durdurulup kopyalanan
  spawner'ın kopyası çalışır geliyordu.** Düzeltme: `CreateDupe` taşınan
  timeout'u ilklendirmeye besliyor ve durumu açıkça taşıyor; `ResetTimer` (her
  iki bileşende) negatif korunmuş değeri **parklanmış durum** sayarak aynı
  deliği yükleme yolunda da kapatıyor. Test: `SpawnerCopyTimerTests` (5) — düz
  eşya kontrolü + aktif/dolu/durdurulmuş üç şekil; taşınan timeout
  etkisizleştirilince beşte üçü kırmızı. Plan bunu da *"henüz doğrulanmış hata
  değildir"* diye taşıyordu. Tam suite 3.677, üç koşu. **PLAN-105 tamamlandı.**
- **İŞ-55 KAPANDI** — 2026-09-13. **Gerçek veri kaybı hatası bulundu ve
  düzeltildi.** Bir spawner'ı kopyalamak kopyaya **orijinalin üye listesini**
  veriyordu; kopyada `STOP` ya da silme, kimsenin dokunmadığı bir spawner'ın
  yaratıklarını yok ediyordu. `CCSpawn::Copy` altı yapılandırma alanını alıp
  *"Not copying created objects"* diye biter (CCSpawn.cpp:1272-1288); üyeler yan
  kapıdan geçiyordu — `ADDOBJ` hiçbir şeyin temizlemediği bir **tag**'e birikiyor,
  yükleme yolu üyeliği o tag'den kuruyor ve kopya kaynağın tüm tag'lerini
  taşıyor. Bir kez kayıttan geçmiş her spawner etkileniyordu, iki türü de.
  Düzeltme: `Item.CreateDupe` bileşeni kurmadan önce kopyadaki tag'i düşürüyor.
  Test: `SpawnerCopyMembershipTests` (5); düzeltmeden önce **beşte dördü
  kırmızıydı**. Plan bunu *"henüz doğrulanmış hata değildir"* diye taşıyordu —
  doğrulandı. Tam suite 3.672, üç koşu. **PLAN-104 tamamlandı.**
- **İŞ-54 KAPANDI** — 2026-09-13. PLAN-103'ün beş ekseni sabitlendi. Kayda
  değer bulgu, **hata gibi görünen ama olmayan bir asimetri:** `CChar::DupeFrom`
  kopyalanan karakteri adlandıran her `MORE1/MORE2/LINK`'i yenisine çevirir
  (CChar.cpp:1222-1229); `CItem::DupeCopy` ise `m_uidLink`'i ham atayıp durur
  (CItem.cpp:4117), yani kendine bağlı bir eşyanın kopyası hâlâ **kaynağa**
  bağlı kalır. Yan yana tutarsızlık gibi okunuyor ve tek satırla
  "düzeltilebilir" — o satır sessiz bir sapma olurdu; iki yön de referans
  satırıyla sabit. Test: `CopyReferenceContractTests` (6). **Üretim değişikliği
  yok** — karakter tarafındaki yeniden işaretleme kapatılınca altıdan tam biri
  kırmızı, eşya tarafındaki doğru şekilde yeşil kalıyor. Tam suite 3.667, üç
  koşu. **PLAN-103 tamamlandı.**
- **İŞ-53 KAPANDI** — 2026-09-13. 13J'nin havuz şişmesi zaten düzeltilmişti ve
  `DuplicationParity13JTests` PLAN-102'nin dört ölçütünden üçünü kapsıyordu;
  dördüncüsünü incelemenin kendisi yapmadığını yazıyordu (*"gerçek kayıt
  deneyi yapılmadı"*). `DuplicatedPoolPersistenceTests` (4) o deney: bonuslu
  takım giymiş bir karakterin kopyası tam kaydet-yükle turundan geçip temel
  100/100/100 ve etkin 120/130/140 dönüyor; ikinci tur değiştirmiyor; yeniden
  başlatmadan sonra takımı çıkarmak 100 bırakıyor; bonussuz kontrol etkilenmiyor.
  **Üretim değişikliği gerekmedi** — garanti zaten sağlamdı. Kaydediciye
  `BaseMaxHits` yerine `MaxHits` yazdırınca dördün üçü kırmızıya dönüyor.
  Tam suite 3.661, üç koşu. **PLAN-102 tamamlandı.**
- **İŞ-52 KAPANDI** — 2026-09-13. Nesne üreten dokuz kapı yan yana kondu.
  Karşılaştırma elle tutulan listeye değil **yansımaya** dayanıyor: `Item`'ın 63
  karşılaştırılabilir property'sinde eşya `DUPE` ve stack bölme hem kaynakla hem
  birbiriyle aynı çıktı. **Bulgu:** referansta `NEWDUPE` kendi başına
  kopyalamaz, `CScript("DUPE")` kurup nesnenin fiilini çağırır
  (CScriptObj.cpp:1311); `CopyParseState` **argümanı taşımadığı** için
  (CScript.cpp:458) CHV_DUPE sıfır okur ve `fNewbieItems = true` verir
  (CChar.cpp:4545) — yani karakterin ekipmanı ATTR_NEWBIE işaretlenir.
  `HandleNewDupe` varsayılanı (false) alıyordu; ATTR_NEWBIE eşya ölümde cesede
  düşmediği için fark gerçek. Belge: `docs/NESNE_URETIM_SOZLESMESI_TR.md`.
  Test: `ObjectCreationContractTests` (8); düzeltme geri alınınca biri kırmızı.
  Açık kalem: çıplak `NEWDUPE`'un çağıranın `ACT`'ini kurması — referansın
  `m_uidNew`'ü kaynağa kurması okumayı belirsizleştirdiği için tahminle
  uygulanmadı. Tam suite 3.657, üç koşu. **PLAN-101 tamamlandı.**
- **İŞ-51 KAPANDI** — 2026-09-13. `docs/reviews/` 106 bölüm / 347 bulgu
  taşıyor ve klasör depoya girmiyor; bir bulgu ancak takip planındaki satırı
  kadar kalıcı. **41 bulgunun** hiç onay kutusu satırı yoktu — takip planının
  numaralandırmasının kapsamadığı başlık biçimleriyle yazılmış, ne açık ne
  kapalı listede duruyorlardı. **Hepsi kapalı çıktı**; 39'u bölüm aralığını
  taşıyan parite test sınıflarında **test adıyla** eşleşti, 12J-2 PreSpawn
  köprüsünde, 12Y-5 ise bulgu bile değil (bölümün kendisi söylüyor).
  **Üretim kodu yazılmadı** — eksik olan defter kaydıydı. Belge:
  `docs/REVIEW_KAYIT_ESLEME_TR.md`. Test: `ReviewRecordGuardrailTests` (3);
  bir satır silinince bulguyu adıyla bildiriyor. Tam suite 3.649, üç koşu.
  **PLAN-005 tamamlandı — Dalga 0 bitti.**
- **İŞ-50 KAPANDI** — 2026-09-13. Port raporu tek bir "100 üzerinden"
  tablosunda sayılan **kapsam**ı ve hiç sayılmamış **sadakat**i yan yana
  koyuyordu; sadakat artık tablonun kendisinde kanaat diye etiketli, kapsam
  hata bandını (±%5) taşıyor. Yöntem bölümü üç gönderim biçimini yazıyor
  (düz literal / son ekli literal / tanımlayıcı-enum): `CScriptObj_functions`
  11-4-30 dağılıyor, yani aynı tablo taramaya göre %20 ya da %83 veriyor.
  Eskimiş sayılar: 3064→3646 test, 148.146/331→153.261/337, property yüzeyi
  470/645 (paydası hiçbir tabloyla eşleşmiyordu) → 390/534, ini 164→162/279,
  AOS 65→77/139, housing 44/71→58/70, lonca taşı 13→19/30. Rapordan sonra
  gelen işler hâlâ eksik listeleniyordu: 43 script adından 16'sı, 19 housing
  fiilinden 9'u (`MOVINGCRATE`, `SECURED`, `GET*POS` ailesi). Test:
  `PortReportGuardrailTests` (6); payda bozulunca kırmızıya dönüyor.
  Tam suite 3.646, üç koşu. **PLAN-004 tamamlandı.**
- **İŞ-49 KAPANDI** — 2026-09-13. Gerçek veriye bağlı testler veri yokken
  erkenden dönüp **BAŞARILI** raporluyordu (xUnit 2'de çalışma anında skip yok),
  yani verisiz bir makinedeki koşu her şeyi ölçen bir koşudan ayrılmıyordu.
  25 dosyadaki 67 kapı `Gate.Missing`/`Gate.MissingValue`'ya taşındı; hangi
  kaynağın istendiği ve bulunup bulunmadığı kaydedilip TRX'in yanına
  `TestResults/data-gates.md`/`.csv` yazılıyor. Bu makinede 66 kapıdan biri
  verisiz: `StairThrowDiagnosticTests` UOP harita karşılaştırması. Kapılar teste
  başarısızlık yazmıyor — veri yokluğu hata değil, görünmezliği hataydı.
  Belge: `docs/VERI_KAPILARI_TR.md`. Test: `DataGateGuardrailTests` (4);
  bir kapı eski sessiz biçimine döndürülünce tarayıcı dosya+satırla yakalıyor.
  Tam suite 3.640, üç koşu. **PLAN-003 tamamlandı.**
- **İŞ-48 KAPANDI** — 2026-09-13. Referansın anahtar tabloları
  `docs/data/sourcex_tables.csv`'ye çıkarıldı (98 tablo, 3570 giriş; sahip tablo,
  enum öneki, sıra ve `ADDPROP` era kapısı dahil). 412 anahtar birden fazla
  tabloda tanımlı (`NAME` onunda), o yüzden tablo toplayan payda aynı anahtarı
  tekrar sayar. Rapordaki üç payda ölçüldü: trigger 248 / 252 / 253 (listeler
  birbirini kapsamıyor), verb 206 giriş ama 186 ayrı anahtar, property 777 / 571
  — rapordaki **645 hiçbiri değil** ve türetilebilir kuralı yok. Belge:
  `docs/SOURCEX_TABLO_PAYDALARI_TR.md`. Test:
  `SourceXTableInventoryGuardrailTests` (6); CSV bozulunca karşılaştırma
  kırmızıya dönüyor. Tam suite 3.636, üç koşu. **PLAN-001 tamamlandı**;
  rapor sayılarının düzeltilmesi PLAN-004'e devredildi.
- **İŞ-47 KAPANDI** — 2026-09-13. `config/sphere.ini`'nin durum işaretleri
  kodla karşılaştırıldı: 18 anahtarın işareti çelişiyordu. 15'i "uygulanmadı"
  diye işaretliyken bir motor yolundan tüketiliyordu (yedisi bu turun kendi
  dalgalarında uygulanmıştı); `CHATFLAGS`/`GENERICSOUNDS` yalnızca `SERV.*`
  yankısı; `ADVANCEDLOS` iki kez atanmış ve kalite kademesi gibi belgelenmişti —
  referansta bit maskesi (`CServerConfig.h:474-476`). Belge:
  `docs/INI_ANAHTAR_SINIFLANDIRMASI_TR.md` (195 anahtar: 165 tüketiliyor, 2
  script'ten okunur, 11 tüketicisiz saklanır, 17 desteklenmez). Test:
  `IniKeyClassificationGuardrailTests` (5); ini düzeltmesi geri alınınca üçü
  kırmızı. Tam suite 3.630, üç koşu. **PLAN-002 tamamlandı.**
- **İŞ-46 KAPANDI** — 2026-09-13. `docs/RELEASE_KABUL_PAKETI_TR.md` +
  `ReleaseAcceptanceGuardrailTests` (6) + docs/README indeksi. Tam suite 3.625,
  üç koşu. **PLAN-705 tamamlandı — Dalga 7 bitti.**
- **İŞ-45 KAPANDI** — 2026-09-13. `CrashDuringSaveDrillTests` (7). Üretim
  değişikliği yok — garanti zaten sağlammış; testler onu sabitliyor. Anlamlı
  oldukları yukleyici çözümü glob'a çevrilerek doğrulandı (1 kırmızı).
  Tam suite 3.619, üç koşu. **PLAN-704 tamamlandı.**
- **İŞ-44 KAPANDI** — 2026-09-13. `LoadProfile` + üç sayac (hareket, trigger,
  kayıt) + `ResetEngineStatics` kaydı. Test: `LoadProfileTests` (10).
  Tam suite 3.612, dört koşu — İŞ-43'ün açık kalemi bu dört koşuda da
  tekrar etmedi. **PLAN-701 ve PLAN-703 tamamlandı.**
- **İŞ-43 KAPANDI** — 2026-09-12. `ExportStatics` sektör-kapsamı (kap içi +
  multi dışlanıyor). Test: `SaveStaticsScopeParityTests` (6); geri alınıp
  4'ünün yakaladığı doğrulandı. Tam suite 3.602. **PLAN-605 ve Dalga 6
  tamamlandı.**
  — **Açık kalem:** 12 tam suite koşusunun **birinde** adı yakalanamayan tek bir
  başarısızlık görüldü; sonraki 11 koşu ve yeni testlerin 5 ayrı stres koşusu
  temiz. Tekrar üretilemedi, kayda geçti.
- **İŞ-42 KAPANDI** — 2026-09-12. `InjectReceived` sona ekliyor +
  `TcpFramingIntegrationTests` (8). Geri alınıp 4'ünün yakaladığı doğrulandı.
  Tam suite 3.596, üç koşu. **PLAN-604 tamamlandı.**
- **İŞ-41 KAPANDI** — 2026-09-12. `PacketBondedStatus` + iki gönderim noktası +
  `docs/PAKET_MATRISI_TR.md`. Test: `BondedStatusPacketParityTests` (3).
  Tam suite 3.588, üç koşu. **PLAN-603 tamamlandı.**
- **İŞ-40 KAPANDI** — 2026-09-12. `docs/AOS_PROPERTY_MATRISI_TR.md` +
  `AosPropertyCoverageGuardrailTests` (3). Tam suite 3.585, üç koşu.
  **PLAN-602 tamamlandı.**
- **İŞ-39 KAPANDI** — 2026-09-12. `docs/BUYU_MATRISI_TR.md` + durum raporundaki
  yanlış etiketin değiştirilmesi + `SpellCoverageGuardrailTests` (3). Tam suite
  3.582, üç koşu. **PLAN-601 tamamlandı.**
- **İŞ-38 KAPANDI** — 2026-09-12. İki gizlilik anahtarı + özel mesaj kapısı +
  `/whois`. Test: `ChatPrivacyParityTests` (10); iki düzeltme tek tek geri alınıp
  doğrulandı (2 + 7). Tam suite 3.579, üç koşu. **PLAN-506 ve Dalga 5 tamamlandı.**
- **İŞ-37 KAPANDI** — 2026-09-12. `GetRecruitRefusal` + `TryAddRecruit`/
  `TryJoinAsMember`; `APPLYTOJOIN` ve `JOINASMEMBER` aynı kapıdan geçiyor.
  Test: `GuildRecruitGateParityTests` (8); kapı geri alınıp 4'ünün yakaladığı
  doğrulandı. Tam suite 3.569, üç koşu. **PLAN-505 tamamlandı.**
- **İŞ-36 KAPANDI** — 2026-09-12. Dümen duruş sebebini söylüyor
  (çalkantılı su / engel), harita-kenarı sınaması engel sınamasından ayrıldı.
  Test: `ShipStopReasonParityTests` (4); geri alınıp 2'sinin yakaladığı
  doğrulandı. Tam suite 3.561, üç koşu. **PLAN-504 tamamlandı.**
- **İŞ-35 KAPANDI** — 2026-09-12. `PreviewCommit` + `@HouseDesignCommit`
  sözleşmesi (ARGN1/2/3 + LOCAL.* + `RETURN 1` vetosu) + değişmemiş-tasarım kapısı.
  Test: `HouseDesignCommitTriggerParityTests` (9), ikisi uçtan uca script'li.
  İki düzeltme tek tek geri alınıp doğrulandı (2 + 2). Tam suite 3.557, üç koşu.
  **PLAN-503 tamamlandı.**
- **İŞ-34 KAPANDI** — 2026-09-12. `ReleaseAllHoldings` +
  `TransferMovingCrateToOwner` silme yolunda; boş sandık her iki yolda siliniyor.
  Test: `HouseTeardownHoldingsParityTests` (8); iki düzeltme tek tek geri alınıp
  doğrulandı (5 + 1). Tam suite 3.548, üç koşu. **PLAN-502 tamamlandı.**
- **İŞ-33 KAPANDI** — 2026-09-12. `ISOWNER` + 8 `GET...POS` +
  `GETSECUREDCONTAINERS`/`GETSECUREDITEMS` + kilit/güvence işaret olayları. Test:
  `HousePermissionKeyParityTests` (17); iki düzeltme tek tek geri alınıp
  doğrulandı (14 + 2). Tam suite 3.540, üç koşu. **PLAN-501 tamamlandı.**
- **İŞ-32 KAPANDI** — 2026-09-12. Öneksiz konut anahtarları + `MOVINGCRATE`
  (get/set/alt-anahtar/kayıt) + redeed'in eldeki sandığı kullanması. Test:
  `HouseMovingCrateParityTests` (12); iki düzeltme tek tek geri alınıp doğrulandı
  (6'şar test). Tam suite 3.523, üç koşu.
- **İŞ-31 KAPANDI** — 2026-09-11. Ölümde büyü dağıtma, ceset ağırlık sınırı,
  ceset suçunda tanık hattı. Test: `DeathDispelAndCorpseWeightParityTests` (5),
  `CorpseCrimeWitnessParityTests` (5); üç düzeltme tek tek geri alınıp
  doğrulandı. Tam suite 3.511, üç koşu. **PLAN-406 tamamlandı.**
- **İŞ-30 KAPANDI** — 2026-09-11. Hayalet-kapı, berserk terk koruması, çıkışta
  gemi durdurma, ahır kapasitesi. Test: `GhostDoorPassageParityTests` (4),
  `PetDesertAndLogoutParityTests` (6), `VendorStableParityTests` genişletildi (+7);
  dört düzeltme tek tek geri alınıp doğrulandı. Tam suite 3.501, üç koşu.
  **PLAN-405 tamamlandı.**
- **İŞ-29 KAPANDI** — 2026-09-11. Görevden alınan satıcının kasası + ek kabı
  sahibin bankasına, dokunulmazlık düşer. Test: `VendorDismissalParityTests` (5);
  geri alınıp dördünün yakaladığı doğrulandı. Tam suite 3.484, üç koşu.
- **İŞ-28 KAPANDI** — 2026-09-11. OF_PETSLOTS kapısı + GM muafiyeti; sınırı sınayan
  8 test dosyası bayrağı kendi bildiriyor. Test: `FollowerCapGateParityTests` (4);
  kapı geri alınıp üçünün yakaladığı doğrulandı. Tam suite 3.479, üç koşu.
- **İŞ-27 KAPANDI** — 2026-09-11. Boş çekiliş düğüme yazılıyor (kendi REGEN'iyle).
  Test: `BarrenResourceNodeParityTests` (3); geri alınıp üçünün de yakaladığı
  doğrulandı. Tam suite 3.475, üç koşu.
- **İŞ-26 KAPANDI** — 2026-09-11. Kalite bandı mesajları, OF_NOITEMNAMING kapısı,
  smelt testi kırılganlığı. Test: `CraftQualityMessageParityTests` (18);
  OF kapısı geri alınıp yakalandığı doğrulandı. Tam suite 3.472, dört koşu.
- **İŞ-25 KAPANDI** — 2026-09-11. ACTIONEFFECT (oku/yaz/yaşam döngüsü) + başarısız
  üretimin üç kaynaklı bedeli. Test: `CraftFailureCostParityTests` (6); iki
  düzeltme tek tek geri alınıp yakalandığı doğrulandı (3/1 kırmızı).
  Tam suite 3.454.
- **İŞ-24 KAPANDI** — 2026-09-11. CheckAntiMagic portu (Mark + gemi dahil),
  SUMMONWALKCHECK, OVERRIDEFIELDS. Test: `SpellRegionAndFlagParityTests` (10);
  üç düzeltme tek tek geri alınıp yakalandığı doğrulandı (2/1/1 kırmızı).
  Tam suite 3.448, üç arka arkaya koşu.
- **İŞ-23 KAPANDI** — 2026-09-11. FREEZEONCAST (global + spell bayrağı), yürürken
  cast, LOWERMANACOST, LOWERREAGENTCOST. Test: `SpellCostAndFreezeParityTests` (10);
  üç düzeltme tek tek geri alınıp yakalandığı doğrulandı (3/2/2 kırmızı).
  Tam suite 3.438, üç arka arkaya koşu.
- **İŞ-22 KAPANDI** — 2026-09-11. Spell_Unequip portu, CAN_I_EQUIPONCAST,
  MAGICF_CASTPARALYZED; ResetEngineStatics'e eksik combat/magic anahtarları.
  Test: `SpellUnequipParityTests` (11); üç düzeltme tek tek geri alınıp
  yakalandığı doğrulandı (2/1/1 kırmızı). Tam suite 3.428, üç arka arkaya koşu.
- **İŞ-21 KAPANDI** — 2026-09-10. @HitParry zardan önce ateşleniyor; ARGN1 yüzde,
  ARGN2/ARGO + dört LOCAL, RETURN 1 vetosu, eşya yıpranması, EFFECT eğrisi.
  Test: `HitParryContractTests` (8); üç düzeltme tek tek geri alınıp yakalandığı
  doğrulandı (7/2/1 kırmızı). Tam suite 3.417.
- **İŞ-20 KAPANDI** — 2026-09-10. Sayısal trigger RETURN'ü, @HitCheck dönüş
  sözleşmesi (oyuncu + NPC yolu), `<SWING>` oku/yaz. Test:
  `HitCheckReturnParityTests` (9); dört düzeltme tek tek geri alınıp yakalandığı
  doğrulandı (3/2/2/1 kırmızı). Tam suite 3.409.
- **İŞ-19 KAPANDI** — 2026-09-10. ATTACKER.* yüzeyi, liste sırası, iki yönlü liste,
  gerçek THREAT, @Attack/@CombatAdd argümanları, öldürme kredisi hasar kapısı.
  Test: `AttackerListParityTests` (9), `CombatEngagementParityTests` (16),
  `AttackerThreatPersistenceTests` (2); dört düzeltme tek tek geri alınıp
  yakalandığı doğrulandı (4/8/1/1 kırmızı). Tam suite 3.400.
- **İŞ-18 KAPANDI** — 2026-09-10. Vendor/secure trade uçtan uca; üç boşluk kapandı
  (eşya TYPE'ı persist, takas penceresi yükleme onarımı, @TradeAccepted REF1..REFn).
  Test: `TradeReloadParityTests` (5), `TradeAcceptedRefsTests` (4) + 56T gerçek veri
  ölçümü; üç düzeltme tek tek geri alınıp yakalandığı doğrulandı. Tam suite 3.373.
- **İŞ-17 KAPANDI** — 2026-09-09. Kap MODMAXWEIGHT'i (oku/yaz/kaydet). Test:
  `ContainerWeightLimitTests` (4). Tam suite 3.364.
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
