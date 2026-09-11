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
| Son güncelleme | 2026-09-11 |
| Son commit | `c04ea58` + İŞ-26/İŞ-27 (üretim + kaynak) |
| Tam test | 3.475 başarılı / 0 başarısız |
| Sıradaki iş | **PLAN-405 (pet/mount/stable)** — PLAN-404 kapandı |

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

## Yapıldı

Bu bölüm yalnızca bu plandaki işlerin kapanışını listeler; bulgu ayrıntısı takip
planındadır.

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
