# SphereNet İnceleme Doğrulama ve Uygulama Planı

Bu dosya, üç inceleme raporunun (`PROJE_GENEL_INCELEME_PLANI_TR.md`,
`HOUSE_SHIP_DEED_SISTEM_INCELEMESI_TR.md`, `PERFORMANS_LOG_INCELEMESI_TR.md` —
üçü de bu dosyaya emildiği için 2026-07-18 doküman temizliğinde silindi; ham
metinleri git geçmişinde) her somut iddiasının **güncel kodla doğrulanmış**
halidir. Açık maddelerin tek yaşayan takipçisi budur. Her madde 3 paralel salt-kod
doğrulama ajanıyla GERÇEK / BAYAT / YANLIŞ / KISMİ olarak sınıflandırıldı; sadece
**GERÇEK** çıkanlar aşağıda aksiyon maddesi olarak listeleniyor.

Sözleşme: bir madde bitince `[x]` işaretle + `(YAPILDI: <kısa kanıt / test>)` ekle.
Line numaraları doğrulama anındaki (2026-07-18) güncel koda göredir; koda dokunulunca
kayabilir — sembol adına güven, satıra değil.

Sonuç: HOUSE_SHIP ve PERFORMANS raporlarındaki **her** kod iddiası GERÇEK çıktı.
PROJE_GENEL'de 1 madde BAYAT (tarım, Wave 270'te düzeltilmiş), 1 madde KISMİ.

**Durum (2026-09-07): açık madde YOK — 296/296 kapalı.** Son taramada açık görünen
on kutu (SX-02-01–04, SX-02B-01–03, SX-03A-01–03) kodda zaten kapanmıştı; düzeltmeler
`f42ea6a` (ticaret) ve `f98612e` (dövüş) commitlerinde yapılmış, kutular
işaretlenmemişti. Her biri koda karşı yeniden doğrulanıp kanıtıyla işaretlendi;
ticaret + dövüş test seçkisi 190/190, tam paket 3143/3143 başarılı.

---

## ELENEN MADDELER (aksiyon YOK)

- **BAYAT — Tarım büyüme döngüsü.** Rapor "PlantSeed son Crops'u doğrudan kuruyor,
  HarvestPlant 60s REAP_TIME, büyüme evresi yok" diyordu; **Wave 270'te tam büyüme
  zinciri eklendi** (`Item.PlantOnTick/PlantCropReset/PlantStartGrowth/PlantDropFruit`,
  `ClientItemUseHandler.PlantSeed`→`PlantStartGrowth`, `HarvestPlant`→`PlantCropReset`,
  `REAP_TIME` kaldırıldı). Aksiyon yok. (Sulama/toprak hâlâ yok ama Source-X'te de yok.)
- **KISMİ — MapViewSize.** Network view range config-driven değil (client 0xC8, clamp
  4-24), AMA `MapViewSize` combat/witness radar için tüketiliyor (`Character.MapViewRadarTiles`,
  `Program.cs:689`). Tamamen kullanılmıyor değil → sadece "view range config'e bağlı değil"
  kısmı P2 olarak C-grubuna eklendi.

---

## A. Başlangıç bağlantı hataları (P0/P1 — sessiz runtime bug, testlerin yakalamadığı)

Ortak sebep: `Program.EngineWiring` çok uzun ve sıraya duyarlı; motorlar oluşturulmadan
önce bağlanıyor. Öneri: tüm nesneler kurulduktan sonra tek "finalize/wiring" aşaması +
zorunlu bağlantılar için fail-fast doğrulama.

- [x] **A1 (P1) — MovementEngine.SpellEngine null bağlanıyor.** (YAPILDI: atama SpellEngine
  oluşturulduktan sonraya taşındı, EngineWiring.) `Program.EngineWiring.cs:472`
  `_movement.SpellEngine = _spellEngine;` ama `_spellEngine` ancak `:1195`'te oluşturuluyor
  (`Program.cs:92` `null!`). `MovementEngine.cs:150` `SpellEngine?.TryInterruptFromMovement`
  null-conditional → **hareket ederek cast-interrupt gerçek sunucuda hiç çalışmıyor.** Fix:
  atamayı SpellEngine oluşturulduktan sonraya taşı.
- [x] **A2 (P1) — NpcAI gece ışığı bağlantısı komple atlanıyor.** (YAPILDI: `GetLightLevel`
  ataması WeatherEngine oluşturulduktan sonraya taşındı, koşulsuz.) `Program.EngineWiring.cs:1716`
  `if (_weatherEngine != null) _npcAI.GetLightLevel = ...` ama `_weatherEngine` `:2040`'ta
  kuruluyor → guard false → atama **hiç yapılmıyor**. `NpcAI.cs:370` `GetLightLevel?.Invoke()
  ?? 0` → hep 0 → **NPC'ler her zaman gündüz varsayıyor** (gece ışık yak/söndür çalışmıyor).
  Fix: WeatherEngine oluşturulduktan sonra bağla.
- [x] **A3 (P1) — `.SHUTDOWN` / `.BROADCAST` no-op.** (YAPILDI: `_commands.OnShutdownCommand`
  → main-loop `_running=false`, `_commands.OnBroadcastCommand` → tüm in-world client'lara
  SysMessage. NOT: event'ler `CommandHandler`'da, SpeechEngine'de değil.) `SpeechEngine.cs:1127/1137` event
  invoke ediyor ama `OnShutdownCommand`/`OnBroadcastCommand`'a **hiçbir yerde `+=` yok**
  (sadece `OnSaveCommand`/`OnResyncCommand` wire'lı). Komut Admin/GM'de kabul edilip sessizce
  hiçbir şey yapmıyor — operasyonda yanıltıcı. Fix: server-side handler bağla.
- [x] **A4 (P1) — Normal shutdown'da save yok.** (YAPILDI: shutdown bloğu `SaveOnShutdown`
  config'ine göre `PerformSave()` yapıyor, default açık, try/catch. Test:
  ConfigRegressionTests.SphereConfig_SaveOnShutdown_DefaultsOnAndParses.) `Program.Tick.cs:261` "Auto-save on shutdown
  is disabled" logluyor; shutdown bloğu (`:257-283`) save yapmıyor. Planlı kapatmada son
  periyodik save'den sonraki değişiklikler kaybolabilir. Fix: güvenli-varsayılan-açık
  configlenebilir shutdown-save.
- [x] **A5 (P0-fixture) — Composition-root doğrulaması.** (YAPILDI — kapsam kararı: test-fixture
  yerine BOOT-TIME fail-fast seçildi, çünkü test fixture'ı grafiği yine taklit ederdi; boot
  doğrulaması her açılışta GERÇEK production grafiğini kontrol eder. `ValidateEngineWiring()`
  `InitializeGameEngines` sonunda 11 zorunlu hook'u doğruluyor (MovementEngine.SpellEngine,
  NpcAI.GetLightLevel/OnWakeNpc, SpeechEngine OnNpcHear/OnItemHear/OnPlayerSpeech,
  shutdown/broadcast komutları — event'ler için Wired probe'ları eklendi —, TriggerDispatcher,
  ObjBase.ResolveWorld, Item.ResolveShipEngine); eksikte isimli InvalidOperationException ile
  boot reddediliyor. Canlı doğrulama: gerçek boot'ta "Engine wiring validated: 11 mandatory
  hooks connected" logu alındı (2026-07-18).)

---

## B. House / Ship / Deed sistemi (P0 bloklayıcı + P1)

- [x] **B1 (P0 — BLOKLAYICI) — MultiReader sadece 12-byte okuyor, format tespiti yok.**
  (YAPILDI: `MultiReader` artık 12/16-byte'ı auto-detect ediyor — index blok uzunluklarının
  strict-divisibility oyu (608%16=0/608%12=8 → HS), tie'da offset-plausibility tiebreak; HS'de
  trailing `ShipAccess` dword okunuyor (`MultiComponent.ShipAccess` eklendi). `ComponentSize`
  property expose edildi. Test: MultiReaderTests (+3: HS16 detect+bounds+shipAccess, orig12
  detect, ambiguous→plausibility→HS).)
  `MultiReader.cs:14` `ComponentSize = 12`, `:60` `dataLength / 12`, `:64` 12-byte read;
  hiçbir 16-byte/High Seas tespiti yok. Source-X iki formatı da modelliyor + auto-detect
  ediyor (`CUOMultiItemRec.h:28/39`, `CUOInstall.cpp:115`, `CServerMap.cpp:618`). 16-byte HS
  veride component offset'leri kayıyor → `MaxY` şişiyor → `PlaceHouse` (`HousingEngine.cs:567`)
  / `PlaceShip` (`ShipEngine.cs:90`) harita-sınırı kontrolünde **terrain'e bakmadan red** →
  "Cannot place here". Fix: `MultiFormat` (Auto/Original12/HighSeas16) + çok-kayıtlı sağlam
  auto-detect (tiledata boyutu + `%12`/`%16` + bounds plausibility) + `shipAccess` dword.
- [x] **B2 (P1) — `ID=i_deed` type inheritance yok.** (YAPILDI: `ResolveDupeItemInheritance`
  artık DisplayIdRef (`ID=<defname>`) için de child Type==Normal ise base TYPE'ı devralıyor —
  Source-X IBC_ID'nin typed base'i dupe etmesinin (IsDupedItem/DUPELIST) aynası. TYPE-only
  (grafik-only ID= referansı Layer/TData sürüklemesin). Source-X `CItemBase.cpp:1659` +
  aktif pack `i_deed_stone_and_plaster_house` (ID=i_deed, TYPE'sız) doğrulandı. Test:
  SourceXDeedInheritanceTests (inherit + no-over-inherit). Tam suite 1836.) `DefinitionLoader.cs:433-457` ID/DISPID
  ref zinciri sadece `DisplayIdRef`/`DispIndex` set ediyor, `Type`'ı kopyalamıyor
  (`ItemDefHelper.cs:51`, `ItemDef.cs:13` default Normal). DUPEITEM'de type inheritance VAR
  (`:925-953`) ama ID= zincirinde yok → scriptten üretilen deed `ItemType.Normal` kalıp deed
  handler'a hiç girmeyebilir. Fix: ID= zincirinde Type/TDATA/Layer devral.
- [~] **B3 (P1) — `[MULTIDEF]` metadata placement registry'ye merge edilmiyor.** (KISMEN
  YAPILDI: `MultiRegistry.MergeScriptMetadata` eklendi — MULTIDEF resource'larının StoredKeys'ini
  paylaşılan multi id ile geometriye overlay ediyor (NAME/TYPE/BaseStorage/BaseVendors);
  `MultiDef`'e bu alanlar eklendi; EngineWiring LoadFromMapData sonrası çağırıyor; PlaceHouse
  artık `def.BaseStorage`'ı house'a uyguluyor (B10'un storage kısmı da kapandı — 400 default
  yerine script değeri). Test: MultiRegistryMetadataTests (+2: merge, no-geometry skip). KALAN
  (büyük, Aşama 4 ile ortak): COMPONENT (dinamik fixture: kapı/sign/tillerman/plank world-item
  üretimi), MULTIREGION/REGIONFLAGS (script region), TSPEECH — bunlar ayrı geometry/fixture
  ayrımı işi.)
  `MultiRegistry.LoadFromMapData` (`HousingEngine.cs:470`) sadece geometri okuyor;
  `EngineWiring.cs:2308` MULTIDEF'i birleştirmiyor → type/name/component/storage/vendor/
  ship-speed placement'ta kayboluyor. (KISMİ: MULTIDEF script resource olarak TSPEECH/
  COMPONENTCOUNT için okunuyor.) Fix: raw multi ID altında binary geometri + script metadata
  merge.
- [x] **B4 (P1) — Yapısal placement result yok.** (YAPILDI: `PlacementFailure` enum
  (PlayerLimit/AccountLimit/MultiMissing/OutOfMap/LocationBlocked/ScriptVeto); PlaceHouse/
  PlaceShip `out failure` overload'u eklendi (eski 3-arg imza korundu → testler kırılmadı),
  her `return null` bir neden set ediyor; deed handler `PlacementFailureMessage` ile neden-özel
  mesaj gösteriyor. Test: SourceXPlacementResultTests. KALAN (ince): su/eğim/overlap ayrımı
  LocationBlocked altında toplu — CanPlace* içinde ayrıştırmak ayrı iş.) House/Ship motorları hep `null` dönüyor
  (`HousingEngine.cs:559-578`, `ShipEngine.cs:77-95`) → tek genel "Cannot place"
  (`ClientItemUseHandler.cs:1219`). Fix: neden-enum'u (limit/format/su/zemin/overlap/LOS/...)
  + ayrı oyuncu mesajı + structured log.
- [x] **B5 (P1) — WorldSaver item TYPE yazmıyor; house/ship raw BaseId'den kurulamaz.**
  (YAPILDI: WorldSaver artık structure item'lar (Multi/MultiCustom/Ship) için numeric `TYPE`
  yazıyor — BaseId raw multi index'i ITEMDEF'siz olduğu için loader def'ten türetemiyordu →
  restart'ta t_normal olup Housing/Ship DeserializeFromWorld bulamıyordu. Loader zaten `TYPE`
  restore ediyor (`case "TYPE"`) + `MaterializeDefinitionType` restore edileni ezmez (`_type==
  Normal` gate). Gerçek `WorldSaver→dosya→WorldLoader` roundtrip test'i eklendi. Test:
  SaveFormatTests.Roundtrip_PreservesMultiStructureType (Multi+Ship). Geriye-uyum: eski save
  TYPE'sız → MaterializeDefinitionType/legacy yolu korunur.)
  `WorldSaver.cs:535-593` ID/TDATA/MORE yazıyor ama instance `ItemType` (Multi/MultiCustom/
  Ship) yok; load'da raw index (`0x64`) normal ITEMDEF sanılabilir. Gerçek `WorldSaver→dosya→
  WorldLoader` roundtrip testi yok (mevcut testler aynı canlı world'de re-read). Fix: STRUCTURE.
  KIND/MULTIID/MULTIDEF persist + gerçek process-boundary roundtrip testi.
- [x] **B6 (P1) — Raw multi ID `0` redeed'de reddediliyor.** (YAPILDI: `TryParseDeedMultiId`'ye
  `allowZero` parametresi eklendi; `SHIP_MULTI_BASEID` branch'i `allowZero: true` geçiyor —
  explicit ship tag'i 0'ı (small ship north raw index) meşru kabul ediyor, dry-dock deed'i tekrar
  açılabiliyor. Ambiguous More1/BaseId fallback hâlâ 0'ı reddediyor.) `TryParseDeedMultiId`
  (`ClientItemUseHandler.cs:3028` `id != 0`) + fallback (`:3013` `targetId==0` fail) →
  dry-dock'tan üretilen classic small ship deed'i (`SHIP_MULTI_BASEID=0`/`More1=0`) tekrar
  açılamıyor. (İlk scripted `MORE=m_small_ship_n` yolu çalışıyor.) Fix: `0`'ı geçerli değer
  say, "yok" için nullable/explicit result.
- [x] **B7 — Yeniden çerçevelendi (recon) + gerçek bug bulundu.** Üç ajan (Source-X, SphereNet
  akışı, ClassicUO) doğruladı: **Source-X multi'yi TEK id ile modelliyor** (base 0x10000; raw
  multi.mul index = `id−0x10000` türetilir; wire art id = 0x4000-based yalnızca send'de). İki ayrı
  alan (RawMultiId/ClientMultiArtId) Source-X'te YOK → planın önerisi yanlıştı, iki-alan ayrımı
  yapılmadı. SphereNet'in raw index'i zaten Source-X'in multi.mul index'ine denk ve görseli
  component-materialize ediyor; save raw index saklıyor. Kullanıcı kararı: raw depolamayı + materialize'ı
  KORU, save'e dokunma. **Gerçek bug client-send sınırındaydı:** multi-tipi item'lar telde multi olarak
  işaretlenmiyordu — `PacketWorldItemSA` (0xF3) data-type byte'ı hardcoded 0, `PacketWorldItem` (0x1A)
  raw id (<0x4000) gönderiyordu; ClassicUO ikisini de statik tile render ediyor. Sonuç: custom-house
  foundation'ları SA istemcilerinde multi olmadığı için 0xD8 design stream sessizce düşüyor → **custom
  evler render olmuyordu.** FIX (boundary encode): MultiCustom item'ları telde multi işaretle —
  0x1A `graphic|0x4000`, 0xF3 `type=2` (id raw kalır, client `&0x3FFF` ile geri alır). Fixed Multi/Ship'e
  dokunulmadı (component-materialize ile çalışıyor; multi göndermek çift-render yapardı). Save değişmedi.
  Test: MultiWirePacketTests. NOT: uçtan uca custom-house render'ı canlı istemcide teyit edilmeli;
  fixed-multi body'sinin origin'de bıraktığı stray-tile ayrı/ön-mevcut kozmetik konu (kapsam dışı).
- [x] **B8 (P1) — 0x99 preview paketi + Source-X anchor-Y düzeltmesi.** (YAPILDI:
  `PacketTargetMulti` (0x99, Source-X send.cpp:1772 wire formatı; HS 7.0.13+ hue dword'lü 30B,
  classic 26B) + `SetPendingMultiTarget` cursor varyantı; deed handler footprint biliniyorsa
  0x99 kaldırıyor (yOff = rect.bottom = MultiDef.MaxY), cevapta Source-X anchor düzeltmesi
  `y -= (bottom-1)` (CItemMulti.cpp:3288) uygulanıyor. Test: HouseShipLightParityTests.
  PacketTargetMulti_WireFormat. Canlı client'ta ghost-preview görsel teyidi önerilir.)
- [x] **B9 (P1) — Target callback güvenlik yeniden-doğrulaması.** (YAPILDI: cursor cevabında
  Source-X OnTarg_Use_Item + CanUse zinciri — deed silinmemiş + cursor kalkarkenki parent'ında
  ("targ moved" anti-cheat) + non-GM için `CanReachTargetItem` + ölü/donmuş reddi; limit'ler
  zaten PlaceHouse/PlaceShip içinde yeniden kontrol ediliyor.)
- [x] **B10 (P2) — House storage sabit 400.** (YAPILDI — B3 kapsamında: `PlaceHouse` artık
  `def.BaseStorage > 0` ise script değerini uyguluyor (`HousingEngine.cs` "Apply script [MULTIDEF]
  BaseStorage"), `HOUSE.STORAGE` tag'iyle persist ediliyor. Test: MultiRegistryMetadataTests.
  Ev-tipi farkı script pack'in MULTIDEF BaseStorage değerlerinden gelir.)
- [x] **B11 (P2) — 0xF6 ship-move paketi component/yolcu listesi taşıyor.** (YAPILDI:
  `PacketBoatSmoothMove` artık Source-X PacketMoveShip (send.cpp:5402) gibi u16 count +
  {serial,x,y,z} listesi yazıyor (boş listede de count alanı var — eskiden hiç yoktu);
  `OnShipMoved` `ListShipObjects` ile güverte objelerini (component/yolcu/kargo, gemi hariç)
  dolduruyor. Test: HouseShipLightParityTests.PacketBoatSmoothMove_CarriesDeckObjectList.)
- [x] **B12 (P2) — Ship su kontrolü.** (YAPILDI — bir kısmı önceki dalgalarda `CanSailInto`
  ile kapanmıştı (bloklayıcı statik + diğer hull); bu dalga: wet STATIC artık su sayılıyor
  (Source-X GetHeightPoint2 CAN_I_WATER katkısı — statik döşenmiş kıyı/liman suyu yüzülebilir)
  + su hattındaki bloklayıcı dinamik world item'lar hull'u durduruyor. Her iki yol (placement
  `:221` + movement `:1017/:1024`) `CanSailInto`'dan geçiyor. Test:
  HouseShipLightParityTests.ShipWater_WetStaticCountsAsSailable.)
- [x] **B13 (P2) — Custom foundation deed tag'inden algılanıyor.** (YAPILDI: `HousingEngine.
  IsCustomFoundation(multiId)` = MULTIDEF `MultiTypeName=="t_multi_custom"` (B3'ten); deed handler
  `customFoundation = CUSTOMHOUSE tag ∨ IsCustomFoundation(multiId)` → ilk foundation deed'i (tag'siz)
  artık MultiCustom açılıyor. Test: SourceXPlacementResultTests.IsCustomFoundation.)
  `ClientItemUseHandler.cs:1175` sadece `CUSTOMHOUSE` deed tag'i; ilk foundation deed'inde tag
  yoksa custom yerine klasik multi açılıyor. Fix: resolved MULTIDEF `t_multi_custom` type'ından
  belirle.

---

## C. Config sözleşmesi (P1/P2 — okunuyor ama uygulanmıyor)

Öneri: her ayarı sınıflandır (uygulanıyor / alias / metadata / deprecated / bağlantısı-eksik);
desteklenmeyen ayar başlangıç uyarısı üretsin.

- [x] **C1 (P2) — TICKPERIOD/ServerTickMs/README uyuşmazlığı.** (YAPILDI: `<TICKPERIOD>` script
  okuması gerçek `ServerTickMs`'i döndürüyor; `TICKPERIOD` ini legacy alias (ServerTickMs yoksa);
  sphere.ini `TICKPERIOD=250`→`ServerTickMs=100` (efektif değere eşit, sürpriz yok). README
  perf-benchmark paragrafı o ölçümün config'i olabilir → dokunulmadı. Test: ConfigRegressionTests
  alias.) `sphere.ini:516 TICKPERIOD=250`
  (yorum "250ms" diyor, yanlış), runtime `ServerTickMs` (default **100**, `Program.Tick.cs:86`),
  `README-TR.md:145` "**50ms**". `TICKPERIOD` ini→ServerTickMs'e hiç parse edilmiyor; script var
  hardcoded "100" (`Program.Scripting.cs:82`). Fix: tek kanonik isim + alias + doc düzelt.
- [x] **C2 (P1) — GameMinuteLength inert.** (YAPILDI: Program.cs `_world.GameMinuteLengthMs =
  GameMinuteLength*1000`; config default 60→20 (mevcut 20s'e hizalı, default sürpriz yok);
  sphere.ini GAMEMINUTELENGTH=8 artık uygulanıyor.) `SphereConfig.cs:498` okuyor, runtime
  `GameWorld.GameMinuteLengthMs` sabit `20_000` (`:1093`), config akmıyor. sphere.ini'deki değer
  (8sn) uygulanmıyor.
- [x] **C3 (P2) — DistanceWhisper/Talk/Yell hardcoded.** (YAPILDI: SpeechEngine const'ları instance
  property'ye çevrildi, EngineWiring config'ten set ediyor (Say←DistanceTalk); config Yell default
  60→48 (mevcut efektife hizalı).) `SpeechEngine.cs:45` const
  Say=18/Whisper=3/Yell=**48**; config (`:408`, Yell **60**) yok sayılıyor (48≠60 kanıt).
- [x] **C4 (P2) — MaxFame/MaxKarma/MinKarma hardcoded clamp.** (YAPILDI: DeathEngine static config
  alanları (default'lar eşit → sürpriz yok), Program.cs config'ten set; clamp literalleri
  değiştirildi.) `DeathEngine.cs:459` literal
  `0,10000`; config (`:222`) referans edilmiyor (varsayılanlar eşit olduğu için şimdilik zararsız).
- [x] **C5 (P2) — MinCharDeleteTime yok sayılıyor.** (YAPILDI: `Character.CreatedUtcSeconds`
  eklendi (char-create'te damgalanıyor), Source-X `CREATE` anahtarıyla (yaş, tenths —
  CChar::r_Write/r_LoadVal birebir) player'lar için persist; `HandleCharDelete` Source-X
  Setup_Delete gate'i uyguluyor (gün cinsinden config, 0x85 reason 3, Counsel+ bypass,
  damgasız legacy char eski sayılır). Test: CharDeleteAndSpellGateTests (2).)
- [x] **C6 (P2) — UseHttp yok sayılıyor.** (YAPILDI: Program.AdminPanel web status `if (_config.UseHttp)`
  ile gate'lendi; config default false→true (mevcut koşulsuz davranış korunur, UseHttp=0 kapatır).) `SphereConfig.cs:716` okunuyor ama tüketen yok;
  `Program.AdminPanel.cs:175` web status'u koşulsuz `Start()` ediyor.
- [x] **C7 (P2) — MapReadId yerine MapSendId ile MUL okunuyor.** (YAPILDI: `_mapData.InitMap`
  artık `MapReadId` kullanıyor (hangi map*.mul); `_world.InitMap` MapSendId (client id). Default
  ikisi de 0 → değişim yok.) `Program.cs:713/716`
  `InitMap(mapDef.MapSendId,...)`; MapReadId sadece validation'da. İkisi farklıysa yanlış MUL
  okunur.
- [x] **C8 (P2) — MapViewSize network view range'e uygulanmıyor.** (YAPILDI: `NetState.DefaultViewRange`
  static'i eklendi, `ViewRange` init'i ondan; Program.cs `MapViewSize`'tan set ediyor. Client 0xC8
  max clamp'ine (24) dokunulmadı — 24→18 sürprizi olmasın. Test: NetStateViewRangeTests.) (KISMİ maddenin aksiyon
  kısmı) `NetState.ViewRange` default 18, sadece client 0xC8 ile değişiyor (clamp 4-24);
  `MapViewSize`/`MapViewSizeMax` view range'e bağlı değil. (Radar için kullanılıyor, dokunma.)

---

## D. Yarım mekanikler (P2)

- [x] **D1 (P2) — Işık kaynakları yaşam döngüsü.** (YAPILDI — Source-X CItem.cpp:6271 birebir:
  yakışta şarj DÜŞMÜYOR (eski davranış düzeltildi), `Item.OnLightBurnTick` 60sn'de bir şarj
  yakıyor (default 20), sıfırda `LIGHT_BURNED` + `ExtinguishLight` (0x4B8/0x3BE douse sesleri)
  ile `LightOut`'a dönüyor, burned kaynak bir daha yakılamıyor, `ATTR_MOVE_NEVER/STATIC`
  sonsuz yanıyor; kalan süre mevcut TIMER persist'iyle restart'ı atlatıyor. Yakma noktaları
  (player use + NPC gece ışığı) timer kuruyor. Test: HouseShipLightParityTests (2). NOT:
  Source-X'in lit/out grafik çifti swap'ı yapılmadı — SphereNet type-flip konvansiyonu korundu.)
- [x] **D2 (P2) — Spell school'ları: minimal sözleşme kapatıldı.** (YAPILDI:
  `SpellEngine.IsInertSchoolSpell` — 201..999 aralığında olup HİÇBİR davranış taşımayan
  (flag yok, effect/duration eğrisi yok, `HasScriptedStages` yok) spell CastStart'ta
  "not supported yet" ile reddediliyor; mana/reagent yanmıyor. Script'li spell'ler
  (flag/eğri/ON= stage → `ResourceLink.HasAnyTriggerBody` üzerinden `SpellDef.HasScriptedStages`)
  etkilenmez. School'ların GERÇEK implementasyonu ayrı büyük proje olarak PARITY.md
  "Deferred tail"de durmaya devam ediyor. Test: CharDeleteAndSpellGateTests.InertSchoolSpell_Classification.)

---

## E. Performans (P0/P1/P2 — hepsi kod düzeyinde GERÇEK)

Not: canlı log analizi ayrıca host/scheduler baskısına da işaret ediyor (yield/net_in
gecikmeleri); aşağıdakiler onu **büyüten** kesin kod borçları.

- [x] **E1 (P0 — YÜKSEK etki / DÜŞÜK efor, EN UCUZ KAZANIM) — StateRecorder her tick full
  `ToArray`.** (YAPILDI: `Tick` imzası `Func<IEnumerable<Character>>` lazy provider'a çevrildi;
  roster SADECE move-scan/snapshot due olduğunda (2s/15s) materialize ediliyor. Caller
  `GetAllObjects().OfType<Character>()` yerine char-only `GetAllCharactersSnapshot` metod
  grubunu geçiyor. Idle tick'te sıfır tahsis. Test: StateRecorderTests.
  Tick_InvokesRosterProviderOnlyWhenScanIsDue.) `Program.Tick.cs:566/788` `_stateRecorder?.Tick(..., _world.GetAllObjects()
  .OfType<Character>())`; `GetAllObjects()` (`GameWorld.cs:1468`) `_objects.Values.ToArray()`
  — argüman interval kontrolünden ÖNCE değerlendiği için ~52K obje dizisi **her tick** (10×/sn,
  ~4MB/s) kopyalanıyor, recorder içinde 2s/15s'de bir tarasa bile. Fix: interval kontrolünü öne
  al **veya** mevcut `GetAllCharactersSnapshot()` (`:1473`) / players-only kullan.
- [x] **E2 (P0) — Save background'a taşındı.** (YAPILDI: `WorldSaver` zaten immutable
  `SaveRecord` snapshot mimarisine sahipti — `Prepare(world)` (main-thread, tek dünya-okuyan faz;
  spheredata içeriği de string'e render ediliyor) / `WritePrepared` (herhangi bir thread:
  shard+encode+yazma+atomik commit) olarak ikiye bölündü. `SAVEBACKGROUND>0` → PerformSave
  capture'dan sonra `Task.Run(WritePrepared)`; tamamlanma yan etkileri (saveCount/hook/broadcast)
  ana döngüde `CompleteBackgroundSave` poll'üyle; üst üste save atlanır; shutdown
  `WaitForBackgroundSave` ile ucuştakini bekler. `SAVEBACKGROUND=0` eski senkron yol.
  `SAVESECTORSPERTICK/SAVESTEPMAXCOMPLEXITY` bilinçli no-op olarak belgelendi (Source-X'in
  tick-başına-aşama modeli yerine snapshot+worker seçildi; Source-X'te de ayrı thread YOK,
  recon doğruladı). Suite yeşil.)
- [x] **E3 (P1 — MED-YÜKSEK / MED) — TIMERF her tick full-world scan.** (YAPILDI: `_objectsWithTimerF`
  active-set — `ObjBase.AddTimerF` mevcut `ResolveWorld` üzerinden `GameWorld.TrackTimerFObject`'e
  kaydediyor (YENİ static YOK; tüm ObjBase world erişimiyle aynı resolver → cross-world riski yok).
  `TickTimerF` artık sadece timer taşıyan objeleri (küçük set) geziyor, tick sırasında boş/silinmiş
  olanları prune ediyor; `DeleteObject` set'ten çıkarıyor. Due-time heap yerine active-set seçildi —
  TIMERF nadir, set küçük, invalidation tek funnel (AddTimerF). Test: PerfIndexTests (ground + contained
  item fire-once + prune; contained case sector-only index'in kaçıracağını kanıtlıyor).)
- [x] **E4 (P2 — MED / MED) — Decay catch-up 5sn'de full-world scan.** (YAPILDI: `_groundItems`
  superset index'i — tek `sector.AddItem` choke point'inden (`PlaceItem`) besleniyor, bu yüzden her
  ground item garantili içeride (load dahil). `CollectExpiredGroundItems` artık `_objects.Values`
  yerine bu set'i geziyor; alınıp cebe konan item'lar (`IsOnGround==false`) tarama sırasında lazy
  prune ediliyor — DecayTime'ın 22 call-site'ına hook GEREKMEDİ. Maliyet obje sayısıyla değil loose
  ground item sayısıyla ölçekleniyor. Test: PerfIndexTests (expired-only collect + picked-up prune).)
- [x] **E5 (P1 — MED / MED) — Sleeping-sector maintenance 3dk'da tek tick'te toplu.** (YAPILDI:
  `TickSleepingSectorItems` (tüm dünyayı tek tick'te gezip her uygun sektöre `OnMaintenanceTick`)
  → `TickSleepingMaintenance(currentTime)` ile değiştirildi: interval bir sweep'i **arm** ediyor,
  sonra her tick **resume cursor**'dan (mapIdx + x + y) sınırlı bir dilim drain ediliyor; tüm grid
  bir kez gezilince sweep idle'a geçip bir sonraki interval'i bekliyor. Per-tick maliyet iki bütçeyle
  sınırlı: `MaintenanceCallsPerTick` (pahalı maintenance tick'i, default 256) + `MaintenanceExaminePerTick`
  (ucuz hücre ziyareti, default 4096). Cadence arm-time'dan ölçülüyor → drain süresi zamanlamayı
  kaydırmıyor. İki call-site (`OnTick` + `OnTickParallel`) de tek metoda indirgendi. Test:
  SleepingMaintenanceBudgetTests — bütçe=1'de iş K tick'e yayılıyor + her uygun sektör tam bir kez
  ziyaret ediliyor; bütçe bol olunca tek tick'te bitiyor.)
- [x] **E6 (P1) — Network bütçeleri.** (YAPILDI: (1) `MaxAcceptsPerPass=32` accept bütçesi —
  fazlası kernel backlog'da bir sonraki pass'i bekler; (2) IP limiti 1100-slot tarama yerine
  Init/Clear'da bakımlı `_ipTally` sayacı; (3) login/unknown bağlantılar da artık non-blocking
  batched send kullanıyor (soket Init'te non-blocking; WouldBlock'ta kalan bytes batch buffer'da
  taşınır, backpressure cap aynı) — zero-window login client flush pass'ini bloklayamaz.
  `NETWORKTHREADS`/`USEASYNCNETWORK` bilinçli no-op olarak SphereConfig'te belgelendi
  (main-loop network tasarımı). Suite yeşil.)
- [x] **E7 (P2) — Auto worker = ProcessorCount.** (YAPILDI: auto default artık
  `max(1, ProcessorCount-1)` — hem `RunMulticoreTick` hem `GameWorld.OnTickParallel`;
  açık `MulticoreWorkerCount` yine kazanır.)
- [x] **E8 (P1 — telemetri) — `snapshot` yanlış-etiket + apply↔flush arası ölçülmeyen işler.**
  (YAPILDI: `world_tick` alt-fazı (OnTickParallel ayrı ölçülüyor; dominant hesabında `snapshot`
  artık grubun kalanı) + `post_apply` fazı (replay/StateRecorder/macro/wheel-reschedule bloğu).
  slow_tick satırı ve /status Telemetry'ye `world_tick`/`post_apply` eklendi. NOT: GameWorld-içi
  daha ince kırılım (timerf_scan/sleeping_maintenance) istenirse ayrı iş.)

---

## F. Atıl altyapı temizliği (P2/P3)

> **Batch 5 kararı (doğrulama sonrası):** F grubu körlemesine "sil" değil — her biri referans
> sayımı + niyet analiziyle incelendi. Gerçekten **redundant/divergence riski** olanlar silindi
> (F3, F4); **kasıtlı ama henüz bağlanmamış** (doğru + testli) scaffolding ise silinmeyip doğru
> sınıflandırıldı (F1, F2, F5) — parity-testli/tutarlı kodu atmak bu projenin "doğrula, uydurma"
> ilkesine aykırı olurdu. Git geçmişi silinenleri korur.

- [~] **F1 (P3) — BotPerformanceGate CI'a bağlı değil → KASITLI (tutuldu).** Doğrulama: bot
  diagnostics alt sistemi CANLI (`BotEngine`, `TickHistogram`, `Program.cs`/`Program.Tick.cs`
  kullanıyor); yalnızca `BotPerformanceGate` (CI-gate eşik değerlendirici) henüz bot-report çıktısına
  ve CI exit-code'una bağlı değil. Doğru + tutarlı bir parça, eksik olan tek şey CI hookup'ı (kapsam
  dışı). Kaldırılmadı; STUB_INVENTORY'de "kasıtlı-bağlanmamış" olarak işaretlendi.
- [~] **F2 (P3) — Fast-walk stack paketleri → KASITLI (tutuldu).** `PacketFastWalkStackInit/Push`
  hiç construct/send edilmiyor AMA `DeferredParityTests.PacketFastWalkStackInit_BuildsExpectedPayload`
  ile wire-format PARITY TESTİ var — kasıtlı, doğrulanmış protokol scaffolding'i. SphereNet zaman
  tabanlı hareket kısıtı kullanıyor; bu paketler ileride era-uyumlu key rotasyonu için hazır. Testli
  kod silinmedi; STUB_INVENTORY'de belgelendi.
- [x] **F3 (P3) — ExpansionInfo tablosu → SİLİNDİ.** `ExpansionInfo.GetInfo` hiç çağrılmıyordu;
  feature mask'leri `GameClient.Login`/`NetState` bağımsız kuruyor. İkinci (divergence riskli) doğruluk
  kaynağıydı → `ExpansionInfo.cs` kaldırıldı. `Expansion`/`FeatureFlags` enum'ları (başka yerde
  kullanılıyor) korundu.
- [x] **F4 (P3) — ExpressionGlobals / ConditionalEvaluator → SİLİNDİ.** İkisi de hiç instantiate/call
  edilmeyen Source-X-şekilli redundant kabuk: gerçek fonksiyon zaten canlı motorda (global VAR/list
  state VarMap/ListMap'te, koşul değerlendirme `ExpressionParser.EvaluateConditional`'da). Bağlanırsa
  ikinci global-state/expression sistemi olurlardı (review'ın uyardığı divergence) → her iki dosya da
  kaldırıldı.
- [~] **F5 (P3) — OnSpeedHackDetected tüketicisi yok → KASITLI (tutuldu).** Doğrulama: speedhack
  ALGILAMA aktif (`ClientCombatHandler:362-379` — verdict, LogWarning, Kick→MarkClosing hepsi çalışıyor).
  `OnSpeedHackDetected` event'i operatörler için deliberate bir extensibility/audit hook'u (yorumu bunu
  açıkça belirtiyor); güvenlik açığı değil, kick yolu bağımsız. Gözlemlenebilirlik zaten log'da mevcut.
  API'den çıkarmak yerine STUB_INVENTORY'de "kasıtlı extensibility hook" olarak belgelendi.

---

## G. Test & doküman boşlukları (P2)

- [x] **G1 (P2) — Üç map diagnostic testi `return` ile "fake pass".** (YAPILDI: xUnit v2'de
  dynamic `Assert.Skip` yok → 3 diagnostic `[Fact]`'e static `[Fact(Skip=...)]` eklendi; artık
  gerçek **Skipped** raporlanıyor (suite 1838 geçti + 3 atlandı, eskiden 3 fake-pass). Local
  çalıştırmak için Skip kaldırılır.)
  `StairThrowDiagnosticTests.cs:29/111/309` `C:\mortechUO\mul` yoksa `WriteLine("SKIP")` + `return`
  → xUnit'te erken return = **passed** (skipped değil). "0 skipped" üç çalışmayan testi gizliyor.
  Fix: gerçek `Skip` (Assert.Skip / SkippableFact) + "Skips cleanly" yorumunu düzelt.
- [x] **G2 (P2) — Trigger dokümanları bayat (NPCSeeWantItem).** (YAPILDI: TRIGGERS.md,
  STUB_INVENTORY_TR.md ve o günkü PARITY_MATRIX.md — sonradan PARITY.md'ye birleştirildi —
  güncellendi; `NPCSeeWantItem` artık ateşleniyor (EngineWiring); güncel tek not-fired char
  trigger `UserVirtue` (virtue-gump, gump yok).) Trigger artık wire'lı+ateşleniyor
  (`Program.EngineWiring.cs:1660/1681`); bayat gösteren doc satırları düzeltildi.

---

## KAPANIŞ (2026-07-18, dalga 3)

Plan kapandı: A(5/5) + B(13/13) + C(8/8) + D(2/2) + E(8/8) + F(5/5) + G(2/2).
Dört madde `[~]`: F1/F2/F5 bilinçli olarak tutuldu (aksiyon yok), **B3'ün ise kalan
işi var** (COMPONENT dinamik fixture, MULTIREGION/REGIONFLAGS, TSPEECH) — Aşama 4
ile ortak, bu satır onu kapalı saymaz.
Son dalga (13 madde, 3 fazda): E7/E8/B10/C5/D2 → B8/B9/B11/B12/D1 → E2/E6/A5.
Her madde: Source-X recon → en küçük kök-neden fixi → build + tam suite (1875 yeşil / 3 skip)
→ çift changelog → commit. A5'in boot doğrulaması gerçek sunucu açılışında teyit edildi
("Engine wiring validated: 11 mandatory hooks connected"). Canlıda izlenecekler:
SAVEBACKGROUND>0 ile save stall'ının capture-süresine inmesi, 0x99 ghost-preview'un
client'ta görünmesi, ışık kaynaklarının 20 dakikada sönmesi.

## Önerilen sıra

1. **A1–A4** (başlangıç bağlantı bug'ları + shutdown-save) — sessiz, testsiz, düşük efor, yüksek
   güven kazanımı. **A5** (composition-root fixture) bunları kalıcı kılar.
2. **E1** (StateRecorder ToArray) — tek satırlık mantık, yüksek etki. **B1** (MultiReader 12/16) —
   house/ship'i modern veride açan bloklayıcı.
3. **E2** (save main-loop dışına) + **E3/E5/E6** (full-world scan / maintenance / network bütçe).
4. **C-grubu** config sözleşmesi (uygulanmayan ayarları bağla/işaretle).
5. **B2–B9** house/ship parity (deed inheritance, MULTIDEF, structured result, save TYPE, raw-0).
6. **D1** ışık yaşam döngüsü; **B10–B13** house/ship P2.
7. **F/G** atıl altyapı temizliği + test/doküman düzeltmeleri.
8. **D2** spell school'ları — ayrı büyük proje (PARITY_BACKLOG ile ortak).

## SX — Kategorili Source-X incelemesi (6 Eylül 2026)

Bu ek bölüm yukarıdaki tarihsel incelemeden ayrıdır; burada üç ajanla doğrulama
yapılmadı. SphereNet `da5972ca`, yerel Source-X `92ced0ba` üzerinde kaynak
karşılaştırması ve izole çalışma senaryoları kullanıldı. Çözüm testleri: 2333/2333.
[Kategori planı](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_KARSILASTIRMA_PLANI.md) ve
[01A kanıt raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_01A_TICARET_OLUM.md)
tarihli kapsam/kanıt kayıtlarıdır; aşağıdaki kutular yaşayan düzeltme durumudur.

- [x] **SX-01A-02 (P1)** — Sahipli satıcıdan dolu çanta tam alımında özgün nesne
  aktarılmalı. (YAPILDI: `ProcessBuy` artık `vendor.OwnerSerial.IsValid` ile Source-X'in
  oyuncu/NPC satıcı dallarını ayırıyor; oyuncu satıcısında tam alım stok nesnesini
  kabından çıkarıp alıcıya veriyor — UID, çocuk nesneler ve tag'ler korunuyor. NPC
  satıcı sanal şablon kolu klonlamaya devam ediyor. Test:
  VendorTransferParityTests.BuyingAFullBagFromAPlayerVendorKeepsItsContentsAndUid,
  APartialBuyFromAPlayerVendorStillLeavesTheRemainder,
  AnNpcVendorStillSellsFromItsVirtualTemplate.)
- [x] **SX-01A-05 (P1)** — İmleçteki düz Blessed eşya. (YAPILDI: SX-01A-04 ile tek
  düzeltme — aşağıya bakın. NOT: bu fark `da5972c` ile açıldı; öncesinde sürüklenen
  eşya hiç aktarılmıyordu ve düz Blessed yanlışlıkla korunuyordu.)
- [x] **SX-01A-01 (P2)** — NPC satıcısında istiflenemeyen çoklu alım. (YAPILDI:
  teslim `stock.IsStackable`'a bakıyor; istiflenemeyen miktar ayrı Amount=1
  nesnelere bölünüyor, her biri aynı taşıma/bırakma yolundan geçiyor. Test:
  VendorTransferParityTests.BuyingThreeNonStackableItemsDeliversThreeObjects,
  BuyingThreeStackableItemsStillDeliversOnePile.)
- [x] **SX-01A-03 (P2)** — Sahipli satıcının aldığı malı VendorExtra'ya aktar.
  (YAPILDI: `ProcessSell` sahipli satıcıda tam satışı özgün nesne olarak
  `Layer.VendorExtra`'ya taşıyor, kısmi satışta satılan miktarın kopyasını koyuyor;
  sahipsiz NPC silmeye devam ediyor. **Raporda olmayan bloklayıcı da giderildi:**
  `WorldSaver` LAYER 27'yi de sanal stok sayıp kayıttan hariç tutuyordu, yani
  transfer tek başına eklenseydi mal her restart'ta buharlaşacaktı — hariç tutma
  yalnız LAYER 26'ya daraltıldı. Test:
  VendorTransferParityTests.SellingToAPlayerVendorMovesTheObjectIntoItsExtraStore,
  SellingToAnOwnerlessNpcVendorStillDestroysTheGoods,
  APlayerVendorsBoughtGoodsSurviveASaveAndLoad.)
- [x] **SX-01A-04 (P2)** — Ölümde korunan ekipmanı çantaya taşı; aktarım sırasını
  eşleştir. (YAPILDI: `DropLootToCorpse` artık Source-X `DropAll` sırasını izliyor —
  **önce çanta, sonra ekipman**; korunan ekipman `KeepWithOwner` ile çantaya
  gidiyor ve çanta zaten boşaltıldığı için orada kalıyor. Sürüklenen eşya da aynı
  geçişte ekipman maskesiyle değerlendiriliyor, bu SX-01A-05'i kapatıyor. Cursor-only
  iptal için `Character.OnDragCancel` eklendi. Test: DeathInventoryEdgeTests
  (AProtectedItemOnTheCursorStaysWithTheOwner artık Newbie/Blessed/Nodropt/NotRading
  teorisi, ProtectedEquipmentIsPackedRatherThanLeftWorn,
  ProtectedEquipmentSurvivesTheEmptiedPack).)
- [x] **SX-01A-06 (P2)** — @Buy/@Sell N2 satır toplamı + @Buy LOCAL.TOTALCOST.
  (YAPILDI: `FilterVendorEntriesByTrigger` N2'ye `amount * price` koyuyor, @Buy'da
  `LOCAL.TOTALCOST` ayakta kalan satırların toplamını taşıyor ve veto edilen satır
  toplamdan düşülüyor. **Bilinçli sapma:** Source-X ARGN2'yi İSTEMCİNİN gönderdiği
  fiyattan kurar; SphereNet işlemin gerçekten tahsil edeceği sunucu fiyatını
  kullanır — parite turunda geri "düzeltilmemeli", kod yorumunda da yazılı. Test:
  GameSystemTests.GameClient_VendorBuyTrigger_PassesLineTotalAndTotalCost,
  GameClient_VendorBuyTrigger_VetoedLineLeavesTheRunningTotal.)

**01A kapanışı (6 Eylül 2026):** altı bulgunun tamamı uygulandı; tam suite
**2.348 başarılı / 0 başarısız**. Sözleşme değişikliği nedeniyle güncellenen mevcut
testler: DeathCorpseParityTests (korunan ekipman artık çantada, giyili değil),
VendorStableParityTests + VendorPacketRoundtripTests (istiflenemeyen çoklu alım
artık ayrı Amount=1 nesneler).

### 01B — kaldirma, birakma, kaplar ve kusanma (6 Eylül 2026)

[01B kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_01B_ENVANTER.md).
Alti bulgunun tamami kodda dogrulandi ve uygulandi.

- [x] **SX-01B-01 (P1)** — Tek surukleme kurali. (YAPILDI: `ResolvePreviousDrag`
  yeni pickup'tan once imlectekini lift-origin'ine geri birakiyor; ayni UID'nin
  tekrar kaldirilmasi Source-X ItemPickup gibi erken donuyor ve ilk origin
  korunuyor. Test: InventoryTransferParityTests.ASecondPickupSettlesTheFirstItem...,
  PickingUpTheSameItemTwiceKeepsTheOriginalDrag.)
- [x] **SX-01B-02 (P1)** — Kap olmayan hedef. (YAPILDI: `IsDropTargetContainer` +
  `RedirectNonContainerTarget`; duz bir esyaya birakma, Source-X
  CClientEvent.cpp:504 gibi o esyanin bulundugu kaba veya zemin karesine
  yonlendiriliyor. Stack birlesmesi ve gercek kap yollari degismedi. Test:
  DroppingOntoAPlainItemDoesNotMakeItAContainer, DroppingOntoARealContainerStillInserts.)
- [x] **SX-01B-03 (P2)** — Bankaya giren cantanin cocuklari. (YAPILDI: kapasite
  kontrolu gelen kabin derin sayisini ekliyor. **Rapordan sapma:** Source-X'te bu
  sayim yalnizca IT_EQ_BANK_BOX koluna ozgu (CItemContainer.cpp:941), bu yuzden
  normal kaplara uygulanmadi — yoksa dolu bir canta bos bir cantaya konamazdi.
  Test: AFullBagCannotWalkPastTheBankItemLimit, AnEmptyBagStillFitsInABankWithRoom.)
- [x] **SX-01B-04 (P1)** — Baska oyuncunun ic cantasi. (YAPILDI: layer'a gore parcali
  kontrol yerine hedef zincirinin KOKU cozuluyor; kok baska bir karakterse ve o
  karakter oyuncunun peti degilse birakma reddediliyor. Test:
  AnItemCannotBePushedIntoAnotherPlayersNestedBag, AnItemCanStillBePutIntoYourOwnPetsPack.)
- [x] **SX-01B-05 (P1)** — Acilmamis kaptan pickup. (YAPILDI: `OpenedContainerRegistry`
  eklendi — Source-X CClient::m_openedContainers karsiligi; SendOpenContainer kaydi
  aciyor, pickup dogruluyor. Kayit kabin acildigi andaki ust-nesnesini ve konumunu
  tutuyor, boylece yer/sahip degistiren kap artik acik sayilmiyor. Oyuncunun kendi
  cantasi/bankasi acik kayit gerektirmiyor (Source-X'te de ust-nesne karakterse
  gecerli). Test: AnItemCannotBeLiftedFromAContainerThatWasNeverOpened,
  AnItemCanBeLiftedOnceTheContainerHasBeenOpened, TheOwnBackpackNeverNeedsAnExplicitOpen,
  AnOpenedContainerThatMovesAwayStopsCounting.)
- [x] **SX-01B-06 (P2)** — Kusanma catismasi lanetli esyayi cikariyor. (YAPILDI:
  `Character.CanEquip` artik dolu layer'daki mevcut esyanin tasinabilirligini
  `ItemMoveRules.CanMove` ile denetliyor ve hicbir mutasyon yapmadan reddediyor
  (Source-X CanEquipLayer, CCharStatus.cpp:470); yeni `EquipDenial.LayerBlocked`.
  Script/loader Equip cagrilarinin zorunlu aktarim semantigi degismedi. Test:
  EquippingOverACursedItemIsRefused, SwappingTwoOrdinaryWeaponsStillWorks.)

**01B kapanisi:** tam suite **2.362 basarili / 0 basarisiz** (+14). Mevcut testlerde
sozlesme degisikligi gerekmedi.

**Kalan (rapor "Devam edecek kontroller"):** pickup miktar normalizasyonu ve trigger
argumanlari, imlec origin bilgisinin harita/teleport sonrasi gecerliligi, nested bank
weight/override kurallari, stack overflow parent limitleri, equip layer'in gercek
itemdef ile eslesmesi. Bunlar 01B'de dogrulanmadi.

### 02 + 02B — guvenli ticaret (6 Eylul 2026)

[02 kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_02_GUVENLI_TICARET.md) ·
[02B kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_02B_KAYIT_SCRIPT.md).
Yedi bulgunun tamami dogrulandi ve uygulandi.

**Kok neden (yedi bulgunun besini birden besliyordu):** trade penceresinin kimligi
yoktu. `InitiateTrade` iki jenerik `Container` yaratiyordu — sahipsiz, konumsuz,
kalici oturum kaydi yok. Source-X `Cmd_SecureTrade` (CClientUse.cpp:1414/1420)
her pencereyi `IT_EQ_TRADE_WINDOW` olarak `LAYER_SPECIAL`'da karaktere kusandirir.
`CreateTradeContainer` artik ayni seyi yapiyor; `ItemType.EqTradeWindow` ve
`Layer.Special` zaten tanimliydi, sadece kullanilmiyordu.

- [x] **SX-02-01 (P1)** — Onay toggle'i. (YAPILDI: `ToggleAccept` → `SetAccept(bool)`;
  paketteki deger ataniyor ve `false` iki tarafi birden temizliyor (Source-X
  Trade_Status, CItemContainer.cpp:144). **Raporda olmayan ek:** Source-X
  receive.cpp:1132 gonderenin KENDI kabini kullandigini da denetler — bu olmadan
  acik-durum atamasi, partnerin bayragini set etmeye izin veren tek tarafli
  tamamlama acigina donusurdu; kontrol eklendi.)
- [x] **SX-02-02 (P2→P1)** — Kendi teklifini geri alamama. (YAPILDI: kok neden
  yukarida; pencere artik sahibine bagli oldugu icin `CanReachInsideContainer`
  kendi penceresini geciriyor, partnerinkini reddediyor. **Bu, `6804d29`'da benim
  actigim regresyondu** — 01B kap erisim kapisi trade penceresini hesaba
  katmiyordu.)
- [x] **SX-02-03 (P1)** — Baslatilamayan ticaret esyayi ortada birakiyor. (YAPILDI:
  `InitiateTrade` artik `bool` donuyor; her erken rette esya lift-origin'ine geri
  birakiliyor ve istemciye `PacketDropAck` yerine reject gidiyor — Source-X
  Event_Item_Drop_Fail, CClientEvent.cpp:325.)
- [x] **SX-02-04 (P1)** — Icerik degisikligi onayi bozmuyor. (YAPILDI: sifirlama
  handler'lardan alinip kap mutasyonuna tasindi — `Item.OnTradeWindowChanged`
  hem `TryAddItem` hem `RemoveItem` icinde tetikleniyor. **Rapora duzeltme:**
  Source-X hem ContentAdd (CItemContainer.cpp:557) hem OnRemoveObj (:798) icinde
  sifirlar; mevcut drop-uzerine sifirlama zaten dogru pariteydi, eksik olan
  cikarma yariydi.)
- [x] **SX-02B-01 (P1)** — Kayittan yuklemede sahipsiz trade kabi. (YAPILDI: yapisal
  yari yukaridaki sahiplik; kurtarma yarisi `RecoverInterruptedTrades` — yuklemeden
  hemen sonra her trade penceresi sahibine bosaltilip kaldiriliyor (Source-X
  CItem::IsWeird → ItemBounce, CItem.cpp:1005). **Rapora duzeltme:** "kaplari
  kayittan haric tut" da "oturumu persist et" de Source-X'in yaptigi degil; referans
  kabi kusanik olarak persist edip yuklemede kendini onariyor. **Raporda olmayan
  tehlike:** `WorldLoader` CONT'suz esyayi kayitli konumuna YERLESTIRIR — yani eski
  davranista pencere, icindeki gercek oyuncu mali ile birlikte (0,0) karesinde
  acilabilir bir dunya kabi olarak geri geliyordu.)
- [x] **SX-02B-02 (P2)** — Cevrimdisi oyuncuyla ticaret. (YAPILDI: `partner.IsOnline`
  kapisi (Source-X CClientUse.cpp:1338 "and also offline players"). Reddedilen
  baslangic SX-02-03'un iade yolunu kullaniyor. NOT: Source-X ayrica karsilikli
  `CanSee` ister; SphereNet'te harita+mesafe var, LOS yok — bu tur eklenmedi,
  02C'ye birakildi.)
- [x] **SX-02B-03 (P2)** — Script vetosu tum ticareti iptal ediyor. (YAPILDI: veto
  artik yalnizca bu degisimi reddediyor, `CancelTrade` cagrilmiyor; pencere ve iki
  teklif yerinde kaliyor (Source-X CItemContainer.cpp:189 duz `return`).
  **Bilincli karar:** Source-X bu dalda iki onay isaretini de SET birakir, biz de
  oyle biraktik — temizlemek farkli bir sozlesme olurdu, kod yorumunda yazili.)

**02 kapanisi:** tam suite **2.374 basarili / 0 basarisiz** (+12). Sozlesme
degisikligi nedeniyle guncellenen mevcut testler: GameSystemTests (ToggleAccept →
SetAccept, `param=0` → `param=1`), TradeSafetyTests (ayni), ve alti `InitiateTrade`
cagri yerinde partnere `IsOnline = true`.

**Kalan (02C):** uzaklasma/harita degisimi, karsilikli CanSee, @TradeAccepted
numarali nesne argumanlari (Source-X m_VarObjs), veto sonrasi tekrar onay akisi,
ticaret surerken save/load'in canli oturumla etkilesimi. Ayrica kapasite
davranisi bilincli tasarim farki olarak birakildi: SphereNet `CanAcceptTradeItems`
ile on-kontrol yapar, Source-X kabulden sonra ItemBounce ile teslim eder.

### 03A — dovus ilk taramasi (6 Eylul 2026)

[03A kanit raporu](D:/Projeler/Yunus/sphereNet/SOURCE_X_BOLUM_03A_DOVUS.md).
Uc bulgunun tamami dogrulandi ve uygulandi; ucu de raporun anlattigindan genisti.

- [x] **SX-03A-02 (P1)** — Yansitilan hasar Invul korumasini atliyor. (YAPILDI:
  `CombatEngine.ApplyReflectedDamage` tek giris; uc yansima dali da (Blood Oath,
  Reactive Armor, REFLECTPHYSICALDAM) buradan geciyor. **Rapora duzeltme:**
  SphereNet'in "fixed hasar recurse edemez" yorumu Source-X'i yanlis okuyordu —
  DAMAGE_FIXED Invul'u atlamaz; recursion'u DAMAGE_REACTIVE engeller
  (CCharFight.cpp:1015), Invul kapisi girişte durur (:642). **Raporda olmayan
  ek dallar:** `SpellEngine` Reactive Armor yansimasi ve `CharacterPoisonState`
  zehir tikleri de bagisikligi hic kontrol etmiyordu; ikisi de kapatildi.
  NOT: `IsDamageImmune` yalnizca Invul+God kapsiyor; Source-X ayrica STATF_STONE,
  CAN_C_FIRE_IMMUNE ve SAFE bolge bayraklarini da kontrol eder — genisletilmedi,
  03B adayi.)
- [x] **SX-03A-01 (P2)** — Mühimmat aramasi kilitli alt kaplara iniyor. (YAPILDI:
  `FindAmmoInContainerCore` artik `IsSearchableContainer` ile iniyor. **Raporda
  olmayan ek:** ayni hata `CraftingEngine`'de sekiz ayri gezicide daha vardi
  (CountInContainerByType, HasItemOfTypeIn, FindItemOfTypeIn, FindInContainerByType,
  FindInContainer, CountInContainer, FindResourceItemByHue, CollectResourceHues) —
  crafting kilitli sandiktan malzeme tuketebiliyordu; hepsi kapatildi.
  **Bilincli istisna:** altin sayimi (`TradeEngine.EnumerateContainerContentsRecursive`)
  DOKUNULMADI — Source-X `ContentConsume` altin icin yalnizca IT_CONTAINER_LOCKED'i
  disliyor, tam IsSearchable kumesini degil (CContainer.cpp:443); oraya bu predikati
  uygulamak yeni bir sapma olurdu.)
- [x] **SX-03A-03 (P2)** — Vurus proc'lari ana hasardan once. (YAPILDI:
  `ApplyAosOnHitEffects` hasar uygulamasindan SONRAYA alindi (Source-X Fight_Hit:
  OnTakeDamage :2259, proc'lar :2270). **Rapora ek — asil zarar gozlemlenen HP
  degil:** proc oldurdugunde silahin kendi hasari hic islenmiyordu ve onun icin
  `RecordAttack` calismiyordu, yani oldurme kredisi, murder count ve loot hakki
  savrulan darbeden proc'a kayiyordu. **Tehlike:** proc cagrisi bagisiklik blogunun
  DISINDA birakildi; Source-X proc'lari `iDmg > 0` ile kapatir ve OnTakeDamage'in
  donusunu yok sayar, iceri tasimak yeni bir sapma olurdu.)

**03A kapanisi:** tam suite **2.388 basarili / 0 basarisiz** (+14). Sozlesme
degisikligi nedeniyle guncellenen mevcut test: `CombatAuditRegressionTests`
`OnHitProcKillStopsTheOriginalStrikePipeline` → `OnHitProcKillStillCreditsTheStrikeThatLanded`
(eski adi ve iddiasi parite disi sirayi sabitliyordu).

**Bu turda ortaya cikan, rapora dahil olmayan fark:** SphereNet Invul bir HEDEFE
hic vurus yapmiyor (`CombatHelper.IsInvalidSwingParticipant` Invul'u gecersiz hedef
sayar), Source-X ise vurup OnTakeDamage icinde sektiriyor — yani Source-X'te
proc'lar Invul hedefe karsi da calisir. Duzeltilmedi (bu turun bulgusu degil);
`CombatParity03ATests.AnInvulnerableTargetIsNotSwungAtAtAll` mevcut davranisi
sabitliyor ve gerekcesini tasiyor. 03B adayi.

### 03B — hazirlik sirasinda durum degisimi ve cephane (6 Eylul 2026)

[03B kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_03B_ZAMANLAMA.md).
Iki bulgu da dogrulandi ve uygulandi.

- [x] **SX-03B-01 (P2)** — Hazirlanmis darbe cozulurken freeze/sleep denetlenmiyor.
  (YAPILDI: `CombatHelper.EvaluateHitTime` artik saldirganin Freeze/Sleeping'ini ve
  HEDEFIN Sleeping'ini yeniden denetliyor. **Rapora duzeltme — disposition:** rapor
  bunlari gecersiz sayiyordu; Source-X `Fight_CanHit` (CCharFight.cpp:1696-1699) bu
  ucu de WAR_SWING_SWINGING dondurur, yani darbe DUSURULMEZ, BEKLETILIR →
  `HitTimeDecision.Wait`. `ClearPendingHit` yanlis olurdu. Yalniz dead/stone/invul/
  insubstantial INVALID'dir ve Drop olarak kaldi. **Rapordaki bir karistirma:**
  `pCharSrc->IsSleeping()` (dormant sektor → INVALID) ile STATF_SLEEPING (→ SWINGING)
  ayni sey degil. **Raporda olmayan ek:** uyuyan hedef SphereNet'te hicbir asamada
  korunmuyordu — `IsInvalidSwingParticipant` Sleeping'e bakmiyor — bu yuzden baslangic
  yollari (`ClientCombatHandler.TrySwingAt`, `NpcAI.TrySwingAttack`) de kapatildi.)
- [x] **SX-03B-02 (P2)** — Menzil disi iska cantadan bolt tuketiyor. (YAPILDI: miss
  dalinda throwing artik hic mühimmat aramiyor. **Rapora ek — sorun throwing'den
  genis:** Source-X'te mühimmat blogu `if (iHitCheck != WAR_SWING_READY) return`
  satirinin ALTINDA (CCharFight.cpp:1814) ve menzil yeniden kontrolu
  WAR_SWING_EQUIPPING dondurur (:1896) — yani "hedef uzaklasti" harcanan bir
  saldiridir ve YAY/ARBALET dahil hicbir mühimmata dokunmaz; ok yalnizca gercek iska
  zarinda tuketilir (:2023) ve yalnizca oyuncu icin (:2041). NPC yolu zaten dogru
  (`FindNpcAmmo` throwing icin null doner, NPC miss dali mühimmat tuketmez) —
  dokunulmadi.)

**03B kapanisi:** tam suite **2.397 basarili / 0 basarisiz** (+9). Mevcut testlerde
sozlesme degisikligi gerekmedi.

**03A'da acilan kusur duzeltildi:** `CombatHelper.FindAmmoInContainerCore` icindeki
uc satirlik yorum `f98612e`'de iki kez yazilmisti; tekillestirildi.

**Bilincli birakilanlar / 03C adaylari:**
- Hidden/Invisible hedef `EvaluateHitTime`'da `Drop` donuyor; Source-X SWINGING
  (bekle) der. Degistirilmedi: gizli kalan bir hedefte "sonsuza kadar beklet"
  bekleyen darbeyi kilitleyebilir; ayri bir tasarim karari.
- Menzil disi durum hala `Miss` olarak siniflaniyor (iska mesaji + sesi uretiyor);
  Source-X harcanan saldiri sayar. Mühimmat tuketimi kaldirildi, siniflandirma
  degismedi.
- `IsCasting` ve `Stam <= 0` yalnizca baslangicta denetleniyor; Source-X
  `Fight_CanHit`'te karsiligi yok, oldugu gibi birakildi.

### 03C — saldiri hizi ve hareket beklemesi (6 Eylul 2026)

[03C kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_03C_HIZ.md).
Iki bulgu da dogrulandi ve uygulandi; raporun aritmetigi bagimsiz olarak yeniden
turetildi ve dogru cikti.

- [x] **SX-03C-01 (P2)** — Hiz formulune temel DEX giriyor. (YAPILDI:
  `GetSwingDelayMs` stat'i era'ya gore seciyor — era 0 `EffectiveDex` (Source-X
  `Stat_GetAdjusted`, CResourceCalc.cpp:61/69), era 1-4 `attacker.Stam` (Source-X
  `Stat_GetVal`, :90/:101/:116/:127). `Stat_GetVal(STAT_DEX)`'in mevcut stamina
  oldugu varsayilmadi, kanitlandi: CChar.cpp:4271 STAM save alanini ondan yazar.
  Formullere ve tarihsel tamsayi yuvarlamalarina dokunulmadi; era 4'un
  `100/(100+SSI)` sifirlanmasi bilincli olarak korundu.
  **Raporda olmayan tehlike:** `Dex` setter'i `_maxStam`'i yukseltir ama mevcut
  havuzu ASLA — yalnizca asagi kirpar. Yani sadece `Dex` verilerek kurulan bir
  karakterin `Stam`'i 0 kalir ve era 1-4'te formulun en yavas degerinden vururdu.
  Uc uretim yolu bunu tohumlamiyordu: `CharDefHelper` (chardef'ten NPC),
  `GameClient.Login` (yeni oyuncu karakteri), `StressTestEngine`. Ucu de duzeltildi;
  setter'a dokunulmadi cunku kod tabaninin kurali "havuzlari yaratan doldurur".)
- [x] **SX-03C-02 (P2)** — Hazirlik sonrasi hareket beklemesi atlaniyor. (YAPILDI:
  `EvaluateHitTime` artik menzilli silahta hareket beklemesini yeniden denetliyor.
  **Disposition:** Source-X `WAR_SWING_EQUIPPING` dondurur (CCharFight.cpp:1854) —
  yani saldiri HARCANIR ve recoil yeniden baslar; bu, 03B'de ekledigim freeze/sleep
  `Wait`'inden FARKLI ve menzil disinin zaten kullandigi `Miss` ile ayni. Yalniz
  menzilli: SphereNet'in melee hareket gecikmesinin Source-X'te karsiligi yok, o
  yuzden hit asamasina tasinmadi.)

**03B'de yarim kalan is tamamlandi:** `f0db737`'de yorum "Source-X harcanan saldiride
mühimmata dokunmaz" diyordu ama kod yalnizca throwing'i disliyordu — yay/arbalet hala
ok tuketiyordu. Miss dali artik hicbir menzilli silahta mühimmat almiyor (Source-X'te
mühimmat blogu :1862, hem menzil (:1896) hem hareket (:1857) donuslerinin ALTINDA).
`Miss` yalnizca harcanan saldiridan uretiliyor; gercek iska zari ayri yoldan gelir.

**03C kapanisi:** tam suite **2.407 basarili / 0 basarisiz** (+10). Sozlesme
degisikligi nedeniyle guncellenen mevcut testler: dort zamanlama testi fixture'i
(`Dex` yanina `Stam` eklendi — beklenen sayilar degismedi) ve
`CombatAuditRegressionTests.StayInRangeRangedMissStillSpendsAmmo` →
`StayInRangeRangedMissDoesNotSpendAmmo` (eski adi ve iddiasi parite disiydi).

**Acik kalan (03D adaylari):** Source-X hareket kontrolunu `m_pClient` ile
kapatir (yalnizca oyuncu); SphereNet `LastMoveTick`'i NPC'ler icin de tutar ve
`ValidateSwingPrep`'te zaten oyuncu ayrimi yok — hit asamasina da ayrimsiz eklendi,
yani iki asama tutarli ama referanstan sapiyor. `IsPlayer` kapisi eklemek ayri bir
karar. Ayrica NPC `@HitTry` Anim/AnimDelay sozlesmesi ve era-specific trigger
davranislari acik.

**Elenen varsayım:** `OYUN_ICI_ANALIZ_RAPORU.md` G04'teki normal çanta içindeki
korumalı eşyanın ayrıca kurtarılması beklentisi Source-X kuralı değil.
`CContainer::ContentsTransfer` yalnızca doğrudan çocukları değerlendirir; güncel
SphereNet'in çantayla birlikte taşıması bu açıdan uyumlu. Bu nedenle G04 için
recursive ölüm koruması uygulanmamalı; shard kuralı istenirse ayrı tasarım kararıdır.

### 04A — buyu kaynagi ve hedefi (6 Eylul 2026)

[04A kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_04A_BUYU.md).
Iki bulgu da bagimsiz olarak dogrulandi ve uygulandi.

- [x] **SX-04A-01 (P1)** — Buyunun KAYNAGI tamamlanmada yeniden cozulmuyordu.
  (YAPILDI: `TryResolveCastSource`, Source-X'in `m_Act_Prv_UID.ObjFind()`
  (CCharSpell.cpp:2882) ve ayni isaretciyi `Spell_CanCast`'e verme (:3010)
  modelini kuruyor. Kaynak yoksa (:2330) ya da ust-duzey sahibi caster degilse
  (:2422) buyu basarisiz. `ConsumeCastSource` artik cozulmus nesneyi aliyor, yani
  baskasinin cantasindaki parsomen tuketilemiyor.
  **GM istisnasi yok:** referans bu kontrolu PRIV_GM kisayolundan ONCE yapar
  (:2422 vs :2429), bu yuzden `IsItemAccessible`'in GM bypass'i kullanilmadi.
  **Raporda olmayan tehlike 1:** `IsCastingWithWand` tag'e degil, O AN KUSANMIS
  esyaya bakiyordu — oyuncu cast suresi boyunca bir asa kusanip siradan bir
  buyuyu asa fiyatina (mana 0, zorluk 10, reagent yok) bitirebiliyordu. Oyuncu
  icin artik yalnizca ETKINLESTIRILEN kaynak sayiliyor; NPC'ler kaynak
  etiketlemedigi icin onlarda kusanmis-asa okumasi korundu.
  **Raporda olmayan tehlike 2:** `ClearCastState` kaynak etiketlerini temizlemiyor,
  ve fizzle / mana-yetersiz / reagent-yetersiz dallari yalnizca onu cagiriyordu —
  WAND_UID/SCROLL_UID caster'in uzerinde kaliyor ve BIR SONRAKI cast o parsomeni
  tuketiyordu. Uc dal da artik etiketleri birakiyor.)
- [x] **SX-04A-02 (P2)** — Gecersiz hedef, maliyetler alindiktan SONRA reddediliyordu.
  (YAPILDI: hedef denetimi fizzle/mana/reagent/kaynak tuketiminin ONUNE tasindi;
  Source-X `Spell_CastDone`'i `Spell_TargCheck` ile acar (CCharSpell.cpp:2878) ve
  tuketime 130 satir sonra ulasir (:3010). Olu hedef (:2740), harita/menzil ve
  "hedef gerekli" (:2728, TARG_XYZ istisnasiyla) kontrolleri orada.
  **Bedava degil:** basarisizlik `FailCastAtCompletion` uzerinden ABORT olarak
  fiyatlaniyor — CCharSkill.cpp:3000 false donusu SKTRIG_ABORT'a cevirir ve
  `Spell_CastFail(fAbort)` MANALOSSABORT/REAGENTLOSSABORT'u uygular (:3316).
  Kosulsuz iade referanstan ayri bir sapma olurdu; rapor da bunu uyariyordu.
  LOS reddi de ayni yola baglandi.)

**04A kapanisi:** tam suite **2.419 basarili / 0 basarisiz** (+12). Her iki
duzeltme gecici olarak geri alinarak testlerin eski davranisi yakaladigi
kanitlandi (kaynak kapisi 3 test, hedef on-denetimi 5 test).
Sozlesme degisikligi nedeniyle guncellenen mevcut testler:
`SpellCastSourceTests` (asa/parsomen artik caster'in cantasinda) ve
`MagicCastFlowParityTests.ScrollCast_ChecksAndConsumesTheSameHalvedCost`
(`SCROLL_UID="0"` hayalet UID'si yerine gercek parsomen) — indirim artik
etiketin varligindan degil cozulmus kaynaktan fiyatlaniyor.

**Acik kalan (04B adaylari):** asa sarj kontrolu (Source-X :2437 sarj yoksa
reddeder; SphereNet CHARGES etiketi olmayan asayi sinirsiz sayar), `ATTR_MAGIC`
kapisi (:2415) ve `CastStart`'in yalniz Layer.OneHanded'a bakan asa okumasi.

### 04B — alan buyulerinde temas (6 Eylul 2026)

[04B kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_04B_ALAN_BUYULERI.md).
Uc bulgu da bagimsiz olarak dogrulandi ve uygulandi.

- [x] **SX-04B-01 (P1)** — Ust uste binmis alanlarin hasari yuruyuste birikiyordu.
  (YAPILDI: Source-X bir konum kontrolunu TEK buyu etkisiyle sinirlar
  (CCharAct.cpp:4996) ve gerekcesini kodda yazar: yigilmis Fire Field'in tek adimi
  katlamasi ve Paralyze+Fire yiginin her hasar tick'inde yeniden dondurmesi.
  SphereNet'te durma yolu ilk alandan sonra donuyordu ama yurume yolu hepsini
  calistiriyordu — iki yol artik ayni siniri paylasiyor.
  **Kritik ayrinti:** sinir denemeyi degil SONUCU izler. Referans
  `fSpellHit = OnSpellEffect(...)` (:5008) atamasini yapar; etki reddedildiginde
  false doner ve sonraki alan hakkini korur. Tek bir "islendi" boolean'i bunu
  ifade edemedigi icin hook `FieldTouchResult` (NotHandled/Handled/SpellHit)
  donuyor. Barrier ve bagisik hedef artik arkasindaki alani yutmuyor.
  **Kapsam:** sinir yalniz buyu alanlarina ait; ayni hucredeki tuzak/telepad
  dongusu Source-X'teki gibi calismaya devam ediyor.)
- [x] **SX-04B-02 (P1)** — Alan temasinda kat/yukseklik ayrimi yoktu.
  (YAPILDI: `Item.IsWithinStepHeight`, Source-X CheckLocation penceresini kuruyor
  (CCharAct.cpp:4934): zdiff = itemZ - charZ, esya yuksekligi en az 3 sayilir,
  zdiff > height veya zdiff < -3 ise esya atlanir. Yurume ve durma yollari ayni
  yardimciyi kullaniyor.
  **Bilincli kapsam genislemesi:** referans bu testi @STEP'ten ONCE yapar, yani
  atlanan esya tetikleyici de calistirmaz. Bu nedenle filtre yalniz alanlara degil
  butun konum dongusune (tuzak, moongate, ag) uygulandi — raporun istedigi
  "paylasilan temas/yukseklik uygunlugu" bu. Alt kattaki moongate'in ust kattaki
  karakteri isinlamamasi da ayni duzeltmenin sonucu.)
- [x] **SX-04B-03 (P2)** — Poison Field, Invul karakterde zehir durumunu baslatiyordu.
  (YAPILDI: Source-X zararli buyu dalinin basinda Invul hedefi, herhangi bir etki
  uygulanmadan geri cevirir ve false doner (CCharSpell.cpp:3762). Fire kendi
  bagisiklik kontrolunu yapiyordu; poison ve paralyze yapmiyordu.
  **Raporda olmayan ikinci hedef:** ayni delik Paralyze Field'da da vardi — Invul
  karakter donuyordu. Referans pakette hem `s_poison_field` hem
  `s_paralyzation_field` SPELLFLAG_HARM tasidigi icin kapi tur listesine degil
  def'in Harm bayragina bagli; ucu birden tek kapidan geciyor.
  **Degismeyen:** zaten devam eden zehir icin tick tarafindaki koruma
  (CharacterPoisonState) korundu — rapor bunun kaldirilmamasini istiyordu.)

**04B kapanisi:** tam suite **2.439 basarili / 0 basarisiz** (+20). Uc duzeltme de
gecici olarak kapatilarak 7 testin eski davranisi yakaladigi kanitlandi.
Sozlesme degisikligi nedeniyle `FieldAndSummonParityTests`'in iki iddiasi
bool'dan `FieldTouchResult.SpellHit`'e guncellendi.

**Acik kalan (04C adaylari):** Source-X zararli dalinin `PLEVEL_Guest` reddi
(:3767) SphereNet'te hicbir yerde yok — Invul kapisiyla ayni satirda ama ayri bir
yetki kurali oldugu icin bu tur eklenmedi. Paralyze Field'in normal Paralyze
tanimina yonlendirilmesi, field uzerinde `@SpellEffect` veto sozlesmesi,
`FIELD_CASTER_UUID` uretilmesine ragmen temasin yalniz UID kullanmasi ve
`OverrideFields` ayari da acik.

### 04C — summon kapasitesi ve kayitli sure (6 Eylul 2026)

[04C kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_04C_SUMMON.md).
Iki bulgu da bagimsiz olarak dogrulandi ve uygulandi.

- [x] **SX-04C-01 (P1)** — Menuden secilen summon'un gercek slot maliyeti kapasite
  kontrolunden SONRA uygulaniyordu. (YAPILDI: Source-X summon'u once secilen
  kimlikten kurar (`CreateBasic(m_atMagery.m_uiSummonID)`, CCharSpell.cpp:2640) ve
  ancak sonra `GetFollowerSlots()` ile olcer (:2662). SphereNet yer tutucuyu
  olcuyor, secimi sonradan uyguluyor ve kapasiteyi bir daha kontrol etmiyordu.
  `SummonCreature` artik secilen tanimi `TryAssignOwnership`'ten ONCE uyguluyor.
  **Ikinci yari - sira:** raporun "basari daline ve basari maliyetine ilerlememeli"
  maddesi. Referans `Spell_Summon_Try`'i :3002'de cagirir, tuketime :3010'da ulasir
  ve tuketim karsilanamazsa summon'u siler (:3012). Summon `PrepareSummon` ile
  butun maliyetlerin onune tasindi; mana/reagent karsilanamazsa `DiscardSummon`
  onu geri aliyor. Basarisizlik `FailCastAtCompletion` ile ABORT olarak
  fiyatlaniyor — kosulsuz iade degil, 04A'daki ayni sozlesme.)
- [x] **SX-04C-02 (P2)** — Summon bitis zamani save'e ham TickCount64 olarak gidiyordu.
  (YAPILDI: mutlak TickCount64 calisma suresidir; yeniden baslatilmis bir makinede
  anlamsizdir. Source-X timer'i KALAN milisaniye olarak yazar (CObjBase.cpp:2081) ve
  yukleme zamanina gore yeniden kurar (:2037). `WorldSaver` artik
  `SUMMON_EXPIRE_REMAINING` yaziyor, `Character.RestoreSummonExpiry` son tarihi
  calisan saatte yeniden kuruyor. Ayni dosyada POISON kaydinin zaten kalan sureyi
  saklamasi ic-emsal olarak izlendi.
  **Eski kayit politikasi:** rapor "eski alani sessizce yok sayip summon'u suresiz
  birakmak dogru degildir" diyordu. Eski mutlak deger yeni saat tabaninda
  okunamadigi icin son tarih summon'un kendi `SUMMON_DURATION` degerinden yeniden
  kuruluyor; suresi de yoksa bir sonraki tick'te bitiyor. En fazla bir sure kadar
  comert, ama SINIRLI.)

**04C kapanisi:** tam suite **2.453 basarili / 0 basarisiz** (+14). Iki duzeltme de
gecici olarak kapatilarak 10 testin eski davranisi yakaladigi kanitlandi. Mevcut
hicbir test sozlesme degisikligi nedeniyle guncellenmedi.

**Test kurulumunda ortaya cikan iki tuzak** (uretim hatasi degil, not olarak):
`DefinitionLoader.LoadAll()` spell registry'sini yeniden kurar — programatik
`Register` ondan SONRA yapilmali. Ve `SpellDef.GetDuration` egrisi
`DurationScale` verilmediginde yuksek skill'de NEGATIFE duser; testte iki uc da
verilmeli.

**Acik kalan (04D adaylari):** dispel/silme sonrasi follower cache, sahibin
olumu/silinmesi, summon `@Create` sirasi, `SummonWalkCheck` ve genel summon sayisi
siniri. Ayrica Source-X follower kapisini `!IsPriv(PRIV_GM)` ve `IsSetOF(OF_PetSlots)`
altinda tutar; SphereNet'in `TryAssignOwnership` kapisinda ikisi de yok — ayri bir
yetki/ayar karari oldugu icin bu tur degistirilmedi.

### 04D — dispel ve summon temizligi (6 Eylul 2026)

[04D kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_04D_SUMMON_TEMIZLIK.md).
Tek bulgu bagimsiz olarak dogrulandi ve uygulandi.

- [x] **SX-04D-01 (P2)** — Silinen summon, sahibinin follower sayisi onbellegini
  gecersizlestirmiyordu. (YAPILDI: Source-X slotu yeniden hesaplamaz, GERI VERIR —
  NPC temizligi `NPC_PetClearOwners` cagirir (CChar.cpp:364) ve yaratigin maliyetini
  sahibinden duser (`FollowersUpdate(this, -iFollowerSlots)`, CCharNPCPet.cpp:597).
  SphereNet sayimi tarayarak yapiyor ve `CurFollowerCacheMs` boyunca tutuyordu;
  kaldirma sahiplik alanlarini yazmadigi icin onbellek kirlenmiyordu.
  **Merkezi nokta secildi:** raporun uyardigi gibi yalniz `DispelConjured`'a eklemek
  sure sonu ve diger silme yollarini acik birakirdi; invalidation butun yollarin
  paylastigi `Character.Delete`'e kondu — referansin da tek bir dispel metodunda
  degil ortak NPC temizliginde yaptigi gibi.
  **Kasitli olarak yapilmayan:** raporun ikinci uyarisi, genel silmede kosulsuz
  `ClearOwnership` cagirmanin silme scriptlerinin eski sahipligi gorme sirasini
  bozabilecegiydi. Bu yuzden sahiplik alanlarina DOKUNULMADI, yalnizca onbellek
  dusuruldu; sonraki sayim yaratigi zaten `IsDeleted` uzerinden atliyor.)

**04D kapanisi:** tam suite **2.462 basarili / 0 basarisiz** (+9). Duzeltme gecici
olarak kapatilarak 6 testin eski davranisi yakaladigi kanitlandi. Mevcut hicbir test
guncellenmedi.

**Acik kalan:** `DispelKillSummons` acikken olum dalinin follower davranisi (bu tur
yalniz dogrudan silme dali calistirildi), silme scriptlerinin veto/yeniden giris
sozlesmesi, sahibin olumu/hesabinin silinmesi/baglanti kopmasi ve summon etkisinin
bagimsiz suresi.

### 05A — tarif kimligi ve uretim aleti (6 Eylul 2026)

[05A kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_05A_URETIM.md).
Iki bulgu da bagimsiz olarak dogrulandi ve uygulandi.

- [x] **SX-05A-01 (P1)** — Ayni display ID'yi kullanan tarifler birbirini eziyordu.
  (YAPILDI: `_recipes` sozlugu artik ITEMDEF KAYNAK kimligiyle anahtarlaniyor —
  Source-X bu kimligi `Skill_MakeItem` boyunca tasir ve tanimi dogrudan onunla bulur
  (CCharSkill.cpp:870/679). `MAKEITEM` de display kimligine indirmek yerine
  `irid.Index` geciriyor.
  **Uyumluluk kurali acikca yazildi:** raporun istedigi gibi eski display-ID lookup'i
  sessizce sonuncuyu secmiyor. `TryGetRecipe` once kaynak kimligine bakiyor; grafik
  uzerinden yalnizca o grafigi TEK bir tarif tasidiginda yanit veriyor, birden
  fazlasi paylastiginda `null` donuyor. Numeric ITEMDEF'ler ve siradan paketler
  birinci veya ikinci daldan cozuluyor.)
- [x] **SX-05A-02 (P2)** — Erisilebilir alet izin verirken kilitli kaptaki alet
  asiniyordu. (YAPILDI: `FindItemOfTypeIn` alt kaba inerken `IsSearchableContainer`
  istemiyordu, `HasItemOfTypeIn` ise istiyordu — izin veren alet ile asinan alet
  farkli olabiliyordu. Source-X `ContentFind` aranamaz kabi atlar
  (CContainer.cpp:236).
  **Raporun onerdigi yapisal cozum uygulandi:** iki helper'i ayri ayri duzeltmek
  yerine `HasItemOfTypeIn` artik `FindItemOfTypeIn` uzerinden cevapliyor; tek arama,
  bir daha ayrisamazlar.
  **Not:** bu, 03A'da cephane ve kaynak yurumeleri icin kapatilan hatanin ikizi —
  o turda gozden kacan tek yurume buydu.)

**05A kapanisi:** tam suite **2.475 basarili / 0 basarisiz** (+13). Iki duzeltme de
gecici olarak kapatilarak 7 testin eski davranisi yakaladigi kanitlandi. Mevcut
hicbir test guncellenmedi (`AllRecipes` anahtari `ushort`'tan `int`'e gecti, ama
disaridaki tek kullanim `GetRecipesBySkill`).

**Acik kalan:** kaynak toplama turu (dugum miktari, basarisizlik tuketimi, uretim
teslim/yerlestirme kosullari) raporun kendi devam kuyrugunda.

### 05B — kaynak toplama script sozlesmesi (6 Eylul 2026)

[05B kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_05B_KAYNAK_SCRIPT.md).
Iki bulgu da bagimsiz olarak dogrulandi ve uygulandi.

- [x] **SX-05B-01 (P1)** — ResourceGather ARGN1'i miktar yerine esya kimligi olarak
  yorumlaniyordu. (YAPILDI: Source-X `Init(wAmount, 0, 0, pResBit)` +
  `LOCAL.ResourceID = m_ReapItem` (CCharSkill.cpp:1029) kurar; ARGN1 MIKTAR, nesne
  argumani marker, esya kimligi local'dir ve tetikleyiciden sonra oradan okunur
  (:1044). Sozlesme birebir kuruldu: `N1 = reapAmount`, `O1 = activeMarker`,
  `Locals["ResourceID"] = reapItemId`.
  **Sifir semantigi:** referans `ConsumeAmount(ARGN1)` ile tuketir ve sonuc <= 0 ise
  esya uretmez. Eskiden 0 "atanmamis" sayilip tam reap veriliyordu; artik hicbir sey
  vermiyor. Negatif de ayni.
  **Kirpma sirasi:** havuz kirpmasi tetikleyiciden ONCE yapiliyor (referans :1025),
  boylece script gercekten sunulan miktari okuyor.
  **Kasitli sapma:** script `LOCAL.ResourceID`'yi 0 veya gecersiz birakirsa tanimin
  kendi reap'i korunuyor; referans o durumda id 0 uretmeye calisirdi.)
- [x] **SX-05B-02 (P2)** — Toplanan esyanin ITEMDEF Create scripti atlaniyordu.
  (YAPILDI: Source-X `CItem::CreateScript` kullanir (CCharSkill.cpp:1050); bu
  `GenerateScript` uzerinden @Create'i calistirir (CItem.cpp:404/415) ve miktari
  ANCAK SONRA atar. `item.FireCreateTrigger()` eklendi; sira da referanstaki gibi
  (Create once, Amount sonra).
  **Raporun iki uyarisi karsilandi:** "iki kez calisabilir" — `FireCreateTrigger`
  instance-guard'li, teslim yolu tekrar cagirsa bile bir kez calisiyor. "Create
  nesneyi silerse olu nesne basariyla teslim edilmez" — `IsDeleted` kontrolu eklendi.)

**05B kapanisi:** tam suite **2.489 basarili / 0 basarisiz** (+14). Iki duzeltme de
gecici olarak kapatilarak 9 testin eski davranisi yakaladigi kanitlandi. Mevcut
hicbir test eski sozlesmeyi kodlamiyordu, guncelleme gerekmedi.

**Bilincli olarak kapsam disi:** referans, kaynak tetikleyicisinden ONCE karakter
uzerinde `CTRIG_RegionResourceGather` calistirir ve ayni arguman/LOCAL havuzunu
paylasir (CCharSkill.cpp:1035). SphereNet'te boyle bir karakter tetikleyicisi HIC
yok — bu, numaralandirilmis bulgunun duzeltmesi degil yeni bir trigger eklemek
olurdu; 04B'deki `PLEVEL_Guest` ve 04C'deki GM/`OF_PetSlots` kapilariyla ayni
gerekce. Ayrica named ITEMDEF/resource kimligi ushort'a indirgeniyor (raporun
"degerlendirilmelidir" notu) ve havuz rejenerasyonu/marker save-load bir sonraki
kesitte.

### 05C - kaynak dugumu yenilenmesi (6 Eylul 2026)

[05C kanit raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_05C_KAYNAK_YENILENME.md).
Iki bulgu da bagimsiz olarak dogrulandi ve uygulandi. **Bu tur canli davranisi
degistiriyor** - ayrintisi changelog'da.

- [x] **SX-05C-01 (P1)** - REGEN zaman birimi ve deger egrisi korunmuyordu.
  (YAPILDI: referans REGEN'i deger egrisi olarak yukler ve yorumunda ONDA SANIYE
  yazar (CRegionResourceDef.cpp:73); zaman asimini
  `GetRandom() * MSECS_PER_TENTH` ile kurar (CWorldMap.cpp:148). SphereNet tek int
  + saniye okuyordu, yani her damar on kat uzun yasiyordu. `ParseExpressionCurve` +
  `GetRandomRegen` eklendi; noktalar ifade olabildigi icin duz `ParseIntegerCurve`
  kullanilamadi (`60*60*10`).
  **Uydurma fallback kaldirildi:** rapor "eski keyfi fallback yeni sozlesmeye
  otomatik tasinmamali" diyordu. Atanmamis REGEN artik sifir orneklenir; referansta
  `MoveToDecay(pt, 0)` neredeyse ani cozulmedir (`CItem.cpp:5879` yorumu).
  **Paket dogrulamasi:** `sphere_region.scp` REGEN=60*60*10 yaziyor ve yanindaki
  "seconds" yorumu ifadeyle celisiyor - `60*60*10` onda saniye olarak tam bir saat.
  Referans C++ carpani esas alindi.)
- [x] **SX-05C-02 (P2)** - Mevcut dugum kademeli doluyor ve omru uzatiliyordu.
  (YAPILDI: referans buldugu bit'i oldugu gibi dondurur (CWorldMap.cpp:71),
  `MoveToDecay`'i yalniz olusturmada cagirir (:148), tuketimde timer'a dokunmaz
  (CCharSkill.cpp:1046) ve sifir miktari tukenmis sayar (:1456). `RegenMarker` ve
  her temastaki deadline itmesi kaldirildi; `RES_MAX`/`RES_LAST` kayitlari da
  gereksiz kaldi.
  **Tasarim karari nasil verildi:** rapor bunu "tasarim karari gerektiren uyumluluk
  farki" olarak isaretlemisti. Kaynagi arastirdim - 55385e9 / changelog
  2026-06-30 girdisi bu davranisi **"Source-X kademeli vein regrow"** paritesi
  olarak kaydetmis. Yani kayitli bilincli bir sapma degil, HATALI bir parite
  iddiasi; bu yuzden parite kazandi ve davranis kaldirildi.
  **Emekli edilen test:** `CraftGatherRemainingTests.RegenMarker_PartiallyRefills...`
  eski sozlesmeyi kodluyordu; kaldirildi ve dosyanin bas yorumu duzeltildi.)

**05C kapanisi:** tam suite **2.499 basarili / 0 basarisiz** (+11 yeni, -1 emekli).
Duzeltmeler gecici olarak kapatilarak 5 testin eski davranisi yakaladigi kanitlandi.
Iki deadline testi once kapatilmis kodda da GECIYORDU - iki toplama ayni
milisaniyede olunca "now + lifetime" iki kez yazmak degismemis gibi okunuyor; testler
sentinel deger kullanacak sekilde saatten bagimsiz hale getirildi.

**Acik kalan:** raporun `RES_LAST` gozlemi kendiliginden cozuldu (alan artik hic
yazilmiyor). Marker `DecayTime`'i `WorldSaver` tarafindan kalan saniye olarak
yazildigi icin 04C sinifindan bir saat-tasima riski kalmadi; yine de gercek yeniden
baslatma + temizleme dongusu canli olarak calistirilmadi.

### 06A-06D - pet komutu, ahir, mount kimligi, figurin (6 Eylul 2026)

Kanit raporlari: [06A](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06A_PET_KOMUT.md),
[06B](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06B_AHIR.md),
[06C](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06C_MOUNT_KIMLIK.md),
[06D](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06D_FIGURIN_SILME.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **SX-06A-01 (P1)** - Pet arkadasligi sahip komutlari icin de yetki veriyordu.
  (YAPILDI: `IsFriendPermittedPetVerb` ile fiil bazli matris. Referans arkadaslara
  TAM OLARAK PC_FOLLOW/PC_STAY/PC_STOP acar (CCharNPCPet.cpp:129-152).
  **Raporun kacirdigi ayrinti:** COME ve FOLLOW ME arkadas kumesinde DEGIL - PC_COME
  (:38) ve PC_FOLLOW_ME (:43) ayri sabitler ve owner-only dala duser. Rapor bunu
  "yalniz FOLLOW, STAY ve STOP" diye gecmisti; birebir uyguladim.
  **Cursor yarisina karsi:** raporun uyardigi gibi yetki hem konusmada hem de hedef
  tiklandiginda yeniden denetleniyor.)
- [x] **SX-06A-02 (P2)** - Come emri eski Go hedefini iptal etmiyordu.
  (YAPILDI: `SupersedePendingPetOrder` yeni her emirde GO_TARGET/PREV_PET_MODE
  dusuruyor. Referans her komutta yeni NPC isi baslatir (:183 / :504).
  **Raporun uyarisi karsilandi:** AI'daki Go onceligi TERSINE CEVRILMEDI - gercek Go
  komutu bozulmasin diye. GO fiili PREV_PET_MODE'u kendi yonetmeye devam ediyor,
  boylece ikinci bir GO detour modunu degil orijinal modu koruyor.)
- [x] **SX-06B-01 (P1)** - Ahir onbellegi yeniden kullanilan owner UID'sini eski
  sahip saniyordu. (YAPILDI: onbellek kaydi artik `(OwnerUuid, Pets)` tutuyor ve
  yalnizca kuruldugu karaktere cevap veriyor.)
- [x] **SX-06B-02 (P2)** - Ahirdan cikan pet eski Attack emrini yeniden basliyordu.
  (YAPILDI: `PetStorage.Park` gecici emirleri dusuruyor.
  **Raporun uyarisi:** kalici pet tag'leri toplu silinmedi - bonding, script durumu
  ve slot override korunuyor.)
- [x] **SX-06C-01 (P1)** - Mount baglantisi silinme ve UID yeniden kullaniminda yanlis
  karaktere baglaniyordu. (YAPILDI: `ResolveMountNpc` - kaydedilmis UUID baglayici,
  serial yalnizca UUID'siz eski kayit icin; Player/silinmis nesne reddediliyor.
  Ayrica mount uzerine `MOUNT_RIDER_UUID` yaziliyor ve `OnMountNpcDeleted` binicinin
  tarafini kopariyor - referansin DeleteCleanup'ta yaptigi (CChar.cpp:395).)
- [x] **SX-06D-01 (P2)** - Figurin yok edilince pet yetim kaliyordu. (YAPILDI:
  `Item.Delete` merkezi yolunda `FigurineDeletedHook`; `PetFigurine.OnFigurineDeleted`
  yalnizca HALA O figurinde park edilmis peti siliyor. `Restore` figurini tuketmeden
  once baglantiyi kopariyor - raporun "kosulsuz pet silme normal acilisi da yok
  edebilir" uyarisi.)

**06 kapanisi:** tam suite **2.518 basarili / 0 basarisiz** (+19). Alti duzeltme de
gecici olarak kapatilarak 11 testin eski davranisi yakaladigi kanitlandi.

**Ortak kok:** alti bulgunun ucu ayni sinifta - kaydedilmis UUID varken serial'a
dusmek. `PetStorage.Resolve` ve `MountEngine.ResolveMountNpc` artik ayni kurali
uyguluyor, boylece ahir/figurin/mount bir daha ayrisamaz.

**Ilk denemede yakalanan tuzak:** iki 06A-02 testi ilk kapatma turunda GECTI - kapatma
kosulunun polaritesini ters yazmisim, yani duzeltme hala aciktil. Polarite duzeltilip
tekrarlandi ve iki test de kirmiziya dondu. Kapatma turunun kendisi de dogrulanmali.

**Acik kalan:** `SOURCE_X_BOLUM_06E_TEKIL_PET_DURUMU.md` bu partide istenmedi.
Ayrica raporlarin kendi kuyruklari: dismount yerlestirme basarisizligi, ayni mount'a
iki rider, zaten park edilmis NPC'nin yeniden paketlenmesi, stable hedefinde gorus
denetimi ve friend/unfriend/release fiillerinin ayri regresyonlari.

### 06E-06H - tekil pet durumu, bonded, besleme, @Eat (6 Eylul 2026)

Kanit raporlari: [06E](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06E_TEKIL_PET_DURUMU.md),
[06F](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06F_BONDED_SAHIPLIK.md),
[06G](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06G_PET_BESLEME.md),
[06H](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06H_EAT_SCRIPT.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **SX-06E-01 (P2)** - Zaten saklanmis/binilmis pet yeniden mount kabul ediyordu.
  (YAPILDI: `TryMount` artik `npc.IsPlayer || npc.IsStatFlag(Ridden)` reddediyor -
  referansta Make_Figurine disconnected yaratigi ve oyuncuyu bastan reddeder
  (CCharAct.cpp:3619), Horse_Mount da onunla basarisiz olur (:3989).
  **Raporun istedigi iki katman:** "yalniz istemci kapisini degistirmek script/motor
  cagrilarini korumaz" - motor kapisi otoriter, `CanSeeCharacterForDoubleClick`'teki
  Ridden reddi ise eski tiklamanin geldigi yolu kapatiyor.)
- [x] **SX-06F-01 (P2)** - Sahipligi biten pet BONDED kaliyordu. (YAPILDI:
  `ClearOwnership` artik `IsBonded = false` yapiyor - referans ayni yerde
  `m_pNPC->m_bonded = 0` yazar (CCharNPCPet.cpp:559).
  **Ortak yol secildi:** rapor "yalniz DeathEngine'de sahiplik sarti eklemek canli
  sahipsiz NPC uzerindeki yanlis BONDED durumunu duzeltmez" diyordu; duzeltme
  release ve desert'i birlikte kapsayan sahiplik temizleme yoluna kondu.
  Tum `ClearOwnership` cagiranlari denetlendi; hepsi gercekten sahipligin bittigi
  durumlar.)
- [x] **SX-06G-01 (P2)** - Tok pete verilen yigin tamamen siliniyordu. (YAPILDI:
  yeni `EatEngine` bos alani olcuyor; tok ise hic yemiyor, kismi ise yalnizca gereken
  adedi tuketiyor, kalan yigin sahibin cantasina donuyor - Use_EatQty :891/:894 ve
  NPC_OnItemGive.)
- [x] **SX-06G-02 (P2)** - FOOD script degeri ile aclik havuzu ayrisiyordu. (YAPILDI:
  `NpcFood` artik `Food` uzerine alias; tek `_food` havuzu. Yan kazanc: `NpcFood` hic
  persist edilmiyordu, artik petin acligi save'i atlatiyor. `MaxFood` MAXFOOD
  etiketinden geliyor - raporun "MAXFOOD=60 sabitini butun yaratiklara yayma"
  uyarisi.)
- [x] **SX-06H-01 (P2)** - Pet beslemesi @Eat'i atliyordu. (YAPILDI: besleme ortak
  `EatEngine` yolundan geciyor, olay yiyen pet uzerinde O1=yiyecek ile calisiyor.)
- [x] **SX-06H-02 (P2)** - Oyuncu @Eat cagrisi ARGN1/LOCAL sozlesmesini uygulamiyordu.
  (YAPILDI: ARGN1 sifirdan baslayan STAT LIMITI, LOCAL.Hits/Mana/Stam/Food hazirlanip
  geri okunuyor (CCharAct.cpp:3456-3476).
  **Raporun isaret ettigi mevcut test duzeltildi:** `P1SkillEventTriggerTests` N1=5
  bekliyordu ve RETURN 1'in yiyecegi de kurtardigini test ediyordu. Referansta ikisi
  de yanlis: ARGN1 stat limiti, ve Use_EatQty EatAnim'den sonra her hâlükârda
  ConsumeAmount cagirir (:913). Iki iddia da referansa gore yeniden yazildi.
  **Bilincli sapma, kayda gecirildi:** EatAnim her local'e mevcut stat'i ekler ve
  toplami UpdateStatVal'e verir; o da mevcut degere BIR KEZ DAHA ekler
  (CCharAct.cpp:757). Bu, her ogunde eldekini ikiye katlar. Duz kazanim uygulandi;
  gerekce EatEngine icinde ve changelog'da yazili.)

**06E-06H kapanisi:** tam suite **2.535 basarili / 0 basarisiz** (+17). Alti duzeltme
de gecici olarak kapatilarak 11 testin eski davranisi yakaladigi kanitlandi; havuz
birlestirmesi icin ayrica ikinci bir kapatma turu yapildi.

**Acik kalan:** `SOURCE_X_BOLUM_06I_ZEHIRLI_YIYECEK.md` bu partide istenmedi.
Ayrica: Source-X `Use_EatQty` zehirli yiyecegi `SetPoison` ile uygular (:905) -
`EatEngine`'e tasinmadi, 06I'nin konusu. FOODVAL'in itemdef VOLUME yarisi SphereNet'te
karsiliksiz; simdilik FOODVAL etiketi ve 10 varsayilani kullaniliyor.

### 06I-06M - zehirli yiyecek ve ceset diriltme on-kontrolu (6 Eylul 2026)

Kanit raporlari: [06I](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06I_ZEHIRLI_YIYECEK.md),
[06J](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06J_VETERINER_DIRILTME.md),
[06K](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06K_CESET_DIRILTME_MENZILI.md),
[06L](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06L_GECERSIZ_CESET_HEDEFI.md),
[06M](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06M_CESET_BOLGE_KURALLARI.md).
Bes bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **SX-06I-01 (P2)** - Zehirli yiyecek yenirken zehir uygulanmiyordu. (YAPILDI:
  `EatEngine.ApplyFoodPoison`, referansin sirasiyla @Eat'ten ONCE (CCharUse.cpp:905).
  **Raporun uyarisi karsilandi:** "POISON_SKILL ham beceri degeri ile ApplyPoison'in
  seviye parametresi ayni birim degildir; dogrudan byte cast yapilmamali" - zehirli
  silah yolunun zaten kullandigi `CombatEngine.CalcOsiPoisonLevel` bantlamasi
  kullanildi, yeni bir eslem uydurulmadi.
  **Yan sonuc:** tok pet hic lokma almadigi icin zehirlenmiyor - referansta da zehir
  Use_EatQty'nin doygunluk reddinden sonra gelir.)
- [x] **SX-06J-01 (P2)** - Savas modunda olmayan olu bonded pet diriltilemiyordu.
  (YAPILDI: kapi artik `target.IsPlayer && !target.IsInWarMode`.
  **Kaynak analizi:** referansin kapisi savas modu degil `STATF_INSUBSTANTIAL`;
  olum bunu yalnizca savas modunda OLMAYAN OYUNCUYA kurar (CCharAct.cpp:4468), NPC'ye
  asla. SphereNet'te hayalet-insubstantial diye bir modelleme yok - `ClientViewUpdater`
  savas modunu manifest sinyali olarak kullaniyor - bu yuzden oyuncu yarisi aynen
  korundu, NPC yarisi kaldirildi.
  **Acik kalan:** SphereNet olumde `Insubstantial` bayragini hic kurmuyor; referansla
  tam hizalama ayri bir hayalet-durum karari.)
- [x] **SX-06K-01 (P2)** - Ceset hedefli diriltme hayaletin konumunu dogrulamiyordu.
- [x] **SX-06L-01 (P2)** - Cozulemeyen ceset hedefi sessizce self-heal oluyordu.
- [x] **SX-06M-01 (P2)** - Cesedin bolgesindeki antimagic bayraklari uygulanmiyordu.
  (Ucu birlikte YAPILDI: yeni `IsCorpseResurrectable` yardimcisi referansin
  CItemCorpse.cpp:28-75 kosullarini tek yerde topluyor ve maliyetten once calisiyor -
  Skill_Healing bunu CCharSkill.cpp:2796'da yapar. Mevcut kap kontrolu de oraya
  tasindi.
  **Raporlarin uyarilari:** sifaci-ceset kontrolleri KALDIRILMADI, hayalet-ceset
  kontrolleri onlarin USTUNE eklendi (06K). `Character.Resurrect` icine Recall/
  NoTeleport eklenmedi - digger diriltme turlerini bozmasin diye ceset yoluna ozel
  on-kontrol tercih edildi (06M). Hedef verilmemis self-heal varsayilani korundu,
  yalnizca "secilmis hedef gecersiz" ondan ayrildi (06L).)

**06I-06M kapanisi:** tam suite **2.550 basarili / 0 basarisiz** (+15). Bes duzeltme de
gecici olarak kapatilarak 10 testin eski davranisi yakaladigi kanitlandi. Mevcut
hicbir test guncellenmedi.

**Acik kalan:** 06M'nin ikinci gozlemi - son asama diriltme reddinin `Healing`
tarafindan basari sayilmasi - bolge on-kontrolu sayesinde bu senaryoda ortadan kalkti,
ama `ResurrectTarget` sozlesmesi hala void; genel sonuc iletimi ayri bir konu.
Ayrica raporlarin kendi kuyruklari: bonding saati, MAXFOOD tanimlari, olu bonded
release, resurrection trigger sonucu, hayalet LOS'unun yanlis aktorle denetlenmesi.

### 06N-06O - MAXFOOD sinirlari ve pet devir temizligi (6 Eylul 2026)

Kanit raporlari: [06N](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06N_MAXFOOD_SINIRLARI.md),
[06O](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_06O_PET_DEVIR_TEMIZLIGI.md).
Uc bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi. 06N, bir onceki turda
eklenen `Character.MaxFood` uzerinde - yani kendi yeni kodumun denetimi.

- [x] **SX-06N-01 (P2)** - CHARDEF MAXFOOD=0 etkili kapasitede 60'a donusuyordu.
  (YAPILDI: `CharDef.MaxFoodExplicit` eklendi; `Character.MaxFood` artik instance
  etiketi (yalniz pozitif) -> acik CHARDEF MAXFOOD (sifir dahil) -> 60 sirasini
  izliyor. Referans instance maksimumu 1'in altindayken tanima doner
  (CCharStat.cpp:276) ve sifir maksimumda Use_Eat besleme reddeder (:934).
  **Guvenlik olcumu - naif uygulama tehlikeliydi:** referans FOODTYPE'tan TURETILEN
  maksimuma da doner. Canli pakette insan/elf/gargoyle chardef'leri
  `FOODTYPE=t_food,t_drink,t_fruit,t_grain` yaziyor; sayisiz girdiler 1 turetiyor
  (`DeriveMaxFood`, referansta `GetResQty()` varsayilani 1). O yari da uygulansaydi
  HER OYUNCUNUN yiyecek tavani 1'e duserdi. Bu yuzden tanimdan yalnizca ACIK MAXFOOD
  cozuluyor; turetilen yari bilincli sapma olarak kayda gecirildi.
  Raporun kendisi de yalnizca acik MAXFOOD tanimlariyla (0/30/100) deney yapmisti.)
- [x] **SX-06N-02 (P3)** - MAXFOOD>60 yaratik 60 toklukla doguyordu.
  (YAPILDI: spawner artik MAXFOOD etiketini Food'dan ONCE yaziyor.
  **Durust not:** bu bulgunun belirtisi zaten 06N-01 tarafindan cozuluyor - tavan
  chardef'ten cozulunce spawn yolunda sira onemsizlesiyor. Kapatma turunda
  `AHeartyDefinitionSpawnsAtItsFullCeiling` sira geri cevrildiginde de GECTI, bunu
  fark edip sirayi bagimsiz sabitleyen ayri bir test yazdim
  (`AnInstanceCeilingMustBeSetBeforeTheValueItCaps`). Yeniden siralama, tavani
  yalnizca instance etiketinden gelen yaratiklar icin hala gerekli.)
- [x] **SX-06O-01 (P2)** - Devredilen pet eski arkadaslari ve bonded durumunu
  tasiyordu. (YAPILDI: `TryAssignOwnership` gercek sahip degisiminde `ClearFriends()`
  ve `IsBonded = false` yapiyor - referans NPC_PetSetOwner uzerinden
  NPC_PetClearOwners cagirir (CCharNPCPet.cpp:600 -> :558).
  **Raporun uc kosulu da karsilandi:** temizlik kapasite kontrolunden SONRA
  (reddedilen devir peti oldugu gibi birakiyor); AYNI sahibin yeniden atanmasinda
  hicbir sey degismiyor (ahir/figurin/dismount yollari); yeni sahip kendi
  arkadaslarini ekleyebiliyor.)

**06N-06O kapanisi:** tam suite **2.563 basarili / 0 basarisiz** (+13). Duzeltmeler
gecici olarak kapatilarak 4 testin eski davranisi yakaladigi kanitlandi; sirali
kapatma turu ayrica 06N-02'nin testinin yetersiz oldugunu ortaya cikardi ve test
guclendirildi.

**Acik kalan:** raporun not ettigi `TickBonding` cagrisizligi - src icinde tanim
disinda cagrilmiyor. Bunun eksik native davranis mi yoksa script tarafindan yonetilen
tasarim mi oldugu Source-X script politikasi dogrulanmadan hata sayilmadi; ayni
degerlendirme burada da gecerli, dokunulmadi. FOODTYPE-turetilen maksimum yarisi da
yukarida kayitli bilincli sapma.

### 07A-07D - kapi, dikey kapi, ozel tanim ve kol-link (6 Eylul 2026)

Kanit raporlari: [07A](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07A_PORTCULLIS.md),
[07B](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07B_OZEL_KAPI_TANIMI.md),
[07C](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07C_KAP_ICINDE_KAPI.md),
[07D](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07D_KOL_KAPI_LINK.md).
Bes bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

**Ortak kok:** Source-X'te birbirinden AYRI uc yordam var - `Use_Portculis` (dikey,
Z hareketi), `Use_DoorNew` (ozel grafik + MOREP), `Use_Door` (klasik mentese) - ve
ikisi de top-level kontroluyle basliyor. SphereNet hepsini tek menteseli rutine
katlamisti; bes bulgunun besi de bu katlamadan cikiyor.

- [x] **SX-07A-01 (P2)** - Portcullis yukseklikleri kullanilmiyor, yalniz grafik
  degisiyordu. (YAPILDI: `UsePortcullis` - MORE1/MORE2 arasinda Z hareketi, grafik
  degismiyor, iki yukseklik esitse no-op (:4596), PORTCULISSOUND override (:4602).)
- [x] **SX-07A-02 (P2)** - Kilitli portcullis normal oyuncunun kullanimini kabul
  ediyordu. (YAPILDI: GM degilse ve link uzerinden gelmiyorsa ret; referansta
  `case IT_PORT_LOCKED` FALLTHROUGH ile `IT_PORTCULIS`'e duser (CCharUse.cpp:1771).)
- [x] **SX-07B-01 (P2)** - DOOROPENID desteklenmiyordu. (YAPILDI: `Item.DoorOpenId`
  (tag-destekli, persist ve scriptten yazilabilir) + `UseCustomDoor`. Grafik takasi ve
  MOREP kaymasi referanstaki gibi; yerine gecen grafik DOOROPENID'ye yaziliyor (:4681).
  **Uygulamada cikan tuzak:** ilk denemede ozel kapi ikinci kullanimda geri gelmiyordu -
  durum klasik `GetDoorDir` tablosundan okunuyor ve alternatif grafik (0x06A5) o
  tabloda KAPALI yuvaya denk geliyordu. Referans `ATTR_OPENED` bayragini okur; ozel
  kapida durum artik yalnizca DOOR_OPEN bayragindan cozuluyor.)
- [x] **SX-07C-01 (P2)** - Kap icindeki kapi haritaya birakiliyordu. (YAPILDI: hem
  `ToggleDoor` hem `Item.CloseDoor` basinda top-level reddi; raporun uyardigi gibi
  yalniz dclick tarafina eklemek timer yolunu acik birakirdi.)
- [x] **SX-07D-01 (P2)** - Kol grafigi degisiyor ama bagli kapi calismiyordu.
  (YAPILDI: `FollowItemLinks` - 64 adim siniri, kayip hedefte ve baslangica donuste
  durma (CCharUse.cpp:1962).
  **Raporun uyarisi karsilandi:** "butun linked hedeflere dogrudan HandleDoubleClick
  cagirmak olmamalidir" - zincir BAGLI kullanim uyguluyor: kapi icin just-open
  (:4641, ikinci cekiste kapanmiyor), kilitli kapi icin link yetkisi (:1771).
  **Bilincli kapsam:** referans MASK_RETURN_FOLLOW_LINKS'i HER item kullaniminda
  doner; zincir simdilik yalniz switch'ten takip ediliyor. Her cift tiklamaya baglamak
  canli shard'larda beklenmedik zincirler uretebilirdi - acik madde olarak birakildi.)

**07 kapanisi:** tam suite **2.579 basarili / 0 basarisiz** (+16). Bes duzeltme de
gecici olarak kapatilarak 10 testin eski davranisi yakaladigi kanitlandi. Mevcut
hicbir test guncellenmedi - 07A raporunun "NpcDoorOpeningTests icinde +2 grafik
bekleyen test var" uyarisi icin kontrol edildi: o testler NPC yolundaki
`DoorHelper.TryOpenDoorState` uzerinden gidiyor ve dokunulmadi.

**Acik kalan:** `DoorHelper.TryOpenDoorState` (NPC kapi acma) hala portcullis icin +2
grafik varsayimini tasiyor - oyuncu yolu duzeldi, NPC yolu ayri bir tur. Ayrica
itemdef door-switch tanimi (SphereNet'te karsiligi yok), DOOROPENSOUND/DOORCLOSESOUND
override'lari ve link zincirinin genel item kullanimina baglanmasi.

### 07E-07G - NPC adim siniri: capraz kose, eskimis rota, inilen Z (6 Eylul 2026)

Kanit raporlari: [07E](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07E_NPC_CAPRAZ_HAREKET.md),
[07F](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07F_NPC_ESKI_ROTA_SICRAMASI.md),
[07G](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07G_NPC_ROTA_YUKSEKLIGI.md).
Uc bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

**Ortak kok:** ucu de "adimi uygula" sinirinda. Source-X'te bu sinirda uc kosul var,
SphereNet'te hicbiri yoktu. Duzeltme tek paylasilan yola kondu: `CanNpcStepTo`
(capraz yan kareler) ve rota adiminda bitisiklik + yuzey cozumu.

- [x] **SX-07E-01 (P2)** - NPC iki engelin birlestigi koseden gecebiliyordu.
  (YAPILDI: `CanNpcStepTo` caprazda iki dik komsu kareyi de sinar
  (CheckValidMove, CCharStatus.cpp:1988). Direction enum'u referansla ayni duzende -
  tek sayilar capraz - bu yuzden `(dir & 1)` testi birebir tasindi.
  **Uygulamada yakalanan asiri-kisitlama:** ilk surumde yan kare kontrolu CANLI
  KARAKTERI de engel sayiyordu ve mesru capraz adimlari bloke etti (kendi rota
  testlerim duserek bunu gosterdi). Referans yan kareleri `CheckValidMove` ile sinar;
  o karakterleri bilmez - engelleyen canlilar yalnizca HEDEF karede tartilir
  (`CanMoveWalkTo` fCheckChars). `CanNpcMoveTo`'ya `checkChars` parametresi eklendi.)
- [x] **SX-07F-01 (P2)** - Yer degistiren NPC eski rotanin uzak karesine sicriyordu.
  (YAPILDI: onbellekteki adim uygulanmadan once ayni harita + tam 1 kare mesafe
  sarti; degilse rota atiliyor (NPC_WalkToPoint, CCharNPCAct.cpp:463).
  **Raporun uyarisi:** genel `MoveCharacter` API'sine kare limiti EKLENMEDI - teleport
  ve diger yerlestirmeler onu kullaniyor; denetim AI adim katmaninda.)
- [x] **SX-07G-01 (P2)** - Rota adimi A*'in yaklasik Z'siyle uygulaniyordu.
  (YAPILDI: `TryResolveNpcStepZ` ile inilen yuzey adimda cozuluyor; Pathfinder'in
  kendi yorumu Z'sinin yaklasik oldugunu zaten soyluyor.
  **Bilincli sinir:** yuzey bulunamadiginda adim reddediliyor, ama yalniz
  `_world.MapData != null` iken - haritasiz (test) dunyada cozucu her kare icin
  "bulunamadi" der ve kosulsuz ret NPC'yi tamamen hareketsiz birakirdi. Bunu da kendi
  testlerim duserek gosterdi.)

**07E-07G kapanisi:** tam suite **2.591 basarili / 0 basarisiz** (+12). Uc duzeltme de
gecici olarak kapatilarak 5 testin eski davranisi yakaladigi kanitlandi. Mevcut hicbir
test guncellenmedi.

**Acik kalan:** raporlarin kendi kuyruklari - kapali kapi ve statik harita duvarlariyla
capraz kural, pet/savas takip akislarinin ayri calistirilmasi, merdiven/multi
yuzeylerinde onbellekteki Z, ve tasima ile rota uretiminin ayni tick'e denk gelmesi.

### 07H-07K - NPC carpisma Z bilinci, gercek adim geometrisi, hareket izni (6 Eylul 2026)

Kanit raporlari: [07H](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07H_NPC_KATLAR_ARASI_ENGEL.md),
[07I](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07I_NPC_UST_KAT_NESNELERI.md),
[07J](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07J_NPC_GECERSIZ_DOGRUDAN_ADIM.md),
[07K](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07K_NPC_HAREKET_KISITLARI.md).
Dort bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

**Raporlarin gormedigi baglanti:** 07J'nin duzeltmesi 07H ve 07I'nin DOGRUDAN-ADIM
yarisini da kapatiyor. Raporlarin kendi olcumleri bunu gosteriyordu - ayni geometride
`WalkCheck` dogru cevap veriyor, yanlis cevap veren tur-tabanli NPC kontroluydu. Bu
yuzden uc ayri Z filtresi yamamak yerine dogrudan adim paylasilan denetime baglandi;
Z filtreleri de A* tarafi ve tile-seviyesi kontrol icin ayrica eklendi.

- [x] **SX-07H-01 (P2)** - Ust kattaki karakter alt kati engelliyordu. (YAPILDI:
  `SharesHeightWith` - referansin bes Z erisimi (CCharAct.cpp:4622); hem
  `CanNpcMoveTo` hem `GameWorld.IsPathTileBlockedByObject` kullaniyor.)
- [x] **SX-07I-01 (P2)** - `IsStaticBlock` butun dikey sutunu kapatiyordu. (YAPILDI:
  `BlocksAtHeight` - esyanin Z + yuksekligi ile yaratigin hacmi kesisiyor mu;
  `Item.DefHeight` 07B turunda eklenmisti, burada ise yaradi. Iki yolda da gecerli.)
- [x] **SX-07J-01 (P2)** - Dogrudan adim gercek yurume geometrisini calistirmiyordu.
  (YAPILDI: `TryNpcStep` - harita verisi varken `WalkCheck.CheckMovement` hem izni hem
  INILEN Z'yi veriyor; onbellekteki A* adimi da ayni yoldan gecip landing ile
  dogrulaniyor. Boylece 07G'de yakalanan "yutulan Found=false" da kapandi.
  **Kapsam:** harita verisi yoksa (test dunyalari) `CheckMovement` her seyi reddettigi
  icin eski tile kontrolleri + capraz kose kurali devrede kaliyor.)
- [x] **SX-07K-01 (P2)** - Freeze/Stone/sifir stamina NPC yuruyordu. (YAPILDI:
  `CanNpcMove` gercek adimda; GM muaf, Freeze/Stone ret, canli yaratikta stamina.
  **Referans ayrintisi:** stamina kapisi MEVCUT havuzu okur (`Stat_GetVal(STAT_DEX)`),
  temel Dex'i degil - 03C'de kurdugum ayrimin aynisi.
  **Kendi testlerimin yakaladigi tehlike:** ilk surum `Stam <= 0` diyordu ve
  `Dex` verilmemis NPC'leri (MaxStam 0, havuz hic doldurulmamis) tamamen dondurdu -
  tam paket 7 gerileme verdi. Kural `MaxStam > 0` ile sinirlandi; stamina modeli
  olmayan yaratik bitkin sayilmiyor.)

**07H-07K kapanisi:** tam suite **2.609 basarili / 0 basarisiz** (+18). Dort duzeltme
de gecici olarak kapatilarak 9 testin eski davranisi yakaladigi kanitlandi. Mevcut
hicbir test guncellenmedi.

**Test yazarken duzelttigim iddia:** engelleme testlerini once "NPC yerinde kalir"
diye yazmistim; gercekte NPC engelli kareye GIRMIYOR ama yanindan capraz dolasabiliyor.
Iddia "engelli kareye girmedi" olarak duzeltildi - dogru olan da bu.

**Acik kalan:** raporlarin kendi kuyruklari - `CAN_C_STATUE` gibi ozel karakter
turleri, ayni kattaki shove/stamina tuketimi, cok katli multi/gemi geometrisiyle uctan
uca takip, NOMOVETILL ve ozel hareket yetenekleri. Ayrica `Wander`/`TrySideStep`
yollarinin kisit kapisini ayri kullanmasi.

### 07L-07Q - guverte/su, gizli hedef takibi, follow trigger, GO ve pet komutlari (6 Eylul 2026)

Kanit raporlari: [07L](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07L_NPC_SU_USTU_PLATFORM.md),
[07M](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07M_PET_GIZLI_HEDEF_TAKIBI.md),
[07N](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07N_PET_FOLLOW_TRIGGER.md),
[07O](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07O_PET_GO_TAMAMLAMA.md),
[07P](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07P_SALDIRI_ONCESI_EMIR_KAYBI.md),
[07Q](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07Q_PET_EQUIP_GUC_KOSULU.md).
Yedi bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **SX-07L-01 (P2)** - Yuzey yerine alttaki su arazisine gore yuzme sarti araniyordu.
  (YAPILDI: `StandsOnWater` - su kurali yalnizca adim gercekten suya iniyorsa gecerli;
  `CanNpcEnterTile` ve `Pathfinder.IsWalkable` ayni yardimciyi paylasiyor.
  **Uygulamada cikan ikinci kapi:** yalniz yuzme kuralini duzeltmek yetmedi - kendi
  tanisal testim gosterdi ki `mapData.IsPassable` de guverteyi gormuyor ve adimi yine
  reddediyordu. `CanNpcOccupy` ayrildi: paylasilan yurume denetimi geometriyi zaten
  onayladiginda, kaba land-seviyesi testleri o onayi geri almiyor.)
- [x] **SX-07M-01 (P2)** - Gizli hedefin guncel konumu kosulsuz okunuyordu. (YAPILDI:
  `ResolveFollowPoint` - gorunur hedefte konum guncellenir ve `FOLLOW_LAST_SEEN`
  etiketine yazilir; gizli hedefte son gorulen yer kullanilir.
  **Kapsam notu:** referansin INT tabanli "takibi birakma" olasiligi tasinmadi - raporun
  cekirdek iddiasi takip noktasinin guncellenmemesiydi; olasilik dali acik madde.)
- [x] **SX-07N-01 (P2)** - Uc durumlu trigger sonucu ve degisebilir argumanlar bool'a
  indirgeniyordu. (YAPILDI: `FollowTriggerResult` + `FollowTriggerArgs`; adapter N1/N2/N3
  besliyor ve geri okuyor.
  **Bilincli sapma:** ARGN2 referansin 1'i yerine SphereNet'in kendi mesafesi (2) ile
  besleniyor - referans degeri koymak hicbir sey scriptlemeyen paketlerde petlerin
  yaklasma mesafesini degistirirdi.
  **Kapatilamayan yari, kayda gecirildi:** `ScriptInterpreter` sifiri Default'a esledigi
  icin gercek SCP `RETURN 0` ile fonksiyon sonuna dusmek ayirt edilemiyor; bunu duzeltmek
  butun trigger'larin RETURN 0 anlamini degistirirdi.)
- [x] **SX-07O-01 (P2)** - `Enum.IsDefined` int/byte uyusmazligi ArgumentException
  firlatiyordu. (YAPILDI: `byte.TryParse` + byte overload. Gercek bir istisnaydi.)
- [x] **SX-07O-02 (P2)** - Bos hedefe bir kare kala varilmis sayiliyordu. (YAPILDI:
  `goDist == 0` varis; ulasilamayan son kare icin "adim atilamadiysa emri bitir"
  korumasi - raporun "sonsuz tekrar yaratma" uyarisi.
  **Mevcut test guncellendi:** `NpcPetGoOrderTests` `distance <= 1`'i varis sayiyordu;
  raporun kendisi de bunun degismesi gerektigini yaziyordu.)
- [x] **SX-07P-01 (P2)** - Saldiri emri kaydettigi onceki modu hemen siliyordu.
  (YAPILDI: `SupersedePendingPetOrder` artik PREV yaziminin ONUNDE.
  **Kendi regresyonum:** bu cagriyi 06A turunda eklemistim ve PREV yaziminin arkasina
  koymusum. 07O ile birlikte ele alinmasi gerekiyordu - PREV geri geldigi anda 07O'daki
  enum istisnasi gorunur hale gelirdi; iki duzeltme ayni turda yapildi.)
- [x] **SX-07Q-01 (P2)** - Pet equip komutu `CanEquip`'i atliyordu. (YAPILDI: komut once
  ortak uygunlugu soruyor; reddedilen esya cantada kaliyor ve tarama devam ediyor.)

**07L-07Q kapanisi:** tam suite **2.633 basarili / 0 basarisiz** (+24). Yedi duzeltme de
gecici olarak kapatilarak 15 testin eski davranisi yakaladigi kanitlandi.

**Acik kalan:** gercek SCP RETURN 0 ayrimi (yorumlayici seviyesinde); referansin INT
tabanli takibi birakma olasiligi; `Wander`/`TrySideStep` ve savas takibinin gizli hedef
kurali; multi gemi guvertesinde hareket eden zemin; iki el catismasi ve ekipman
paketleri.

### 07R-07U - pet ekipmani: iki el, trigger, yigin ve drop all (6 Eylul 2026)

Kanit raporlari: [07R](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07R_PET_IKI_EL_KUSANMA.md),
[07S](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07S_PET_EQUIP_TRIGGER.md),
[07T](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07T_PET_STACK_KUSANMA.md),
[07U](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07U_PET_DROP_ALL_EKIPMAN.md).
Dort bulgu da bagimsiz olarak dogrulandi; hepsi ayni `equip`/`drop` dallarinda oldugu
icin tek turda uygulandi. Sozlu equip artik referanstaki `ItemEquip` sirasini izliyor.

- [x] **SX-07R-01 (P2)** - Pet ayni anda kilic ve iki elli yay tutabiliyordu. (YAPILDI:
  `OtherHandIsTaken` - HAND2'ye giden silah HAND1'i, HAND1 equip'i HAND2'deki silahi
  gozetiyor; kalkan disarida.
  **Raporun otesinde bulunan:** `Character.Equip` iki elli silahi OneHanded'dan
  TwoHanded'a terfi ettiriyor, yani tiledata'sina gore tek elli gorunen bir yay eski
  kodda dogrudan `Equip`'e dusup kalkani cantaya iterdi. Katman artik terfi SONRASI
  puanlaniyor.
  **Bilincli tercih:** dolu el soyulmuyor, esya atlaniyor - referansta ItemEquipWeapon
  zaten yalnizca iki el de bosken silah arar (CCharUse.cpp:2051); raporun "lanetli/
  tasinamaz esyayi zorla cikarma" uyarisi da bu yonu isaret ediyor.
  **Kurulumda cikan tuzak:** `Item.IsTwoHanded` giyili esyada KATMANDAN okunuyor, yani
  TwoHanded'daki kalkan da "iki elli" gorunuyor. Kars el testi bu yuzden yalnizca
  `IsWeaponType` soruyor - ilk surumum kalkan+kilic ikilisini bozmustu, test yakaladi.)
- [x] **SX-07S-01 (P2)** - Sozlu equip @EquipTest ve @Equip tetiklemiyordu. (YAPILDI:
  veto once, hicbir sey tasinmadan; @Equip esya giyildikten sonra. Callback esyayi
  silerse veya cantadan cikarirsa esya kusanilmiyor - referansin CCharAct.cpp:3331
  denetimi.)
- [x] **SX-07T-01 (P2)** - Yigin butun halinde kusaniliyordu. (YAPILDI: `UnStackSplit(1)`
  karsiligi - giyilen parca asil kimligi korur, kalan tam kopya olarak cantada kalir.
  Raporun kendi ek istegi olan tag/dayaniklilik korunmasi da testte.)
- [x] **SX-07U-01 (P2)** - `drop all`, `drop` ile ayni canta dongusune indirgenmisti.
  (YAPILDI: once canta yere dokuluyor, SONRA ekipman cantaya aliniyor - raporun ozellikle
  uyardigi sira. Bos canta artik komutu erken bitirmiyor.
  **Raporun otesinde ele alinan iki yan kural:** conjured yaratik `drop all` ile hicbir
  sey birakmiyor (CCharAct.cpp:567) ve iki drop komutu da ATTR_OWNED/NEWBIE/MOVE_NEVER/
  CURSED2/BLESSED2 esyalarini cantada birakiyor (CContainer.cpp:502). Rapor ikisini de
  "ayrica dogrulanmadi" diye ayirmisti; ayni referans fonksiyonunda olduklari icin
  birlikte kapatildi.)

**Bulunan ve duzeltilen kendi hatam:** 07O-02 regresyon testi
(`AnUnreachableLastTileEndsTheOrderRatherThanRetryingForever`) CI'da kizardi. Sebep uretim
kodu degil testin kendisiydi: engellenen adima verilen yan adim rastgeledir (referansin
kendi zari, CCharNPCAct.cpp:497), yani emrin bitmesi %30'luk bir role bagliydi ve yerelde
10 tikta oluyordu, CI'da olmadi. Test artik yaratigi sekiz komsusuyla kapatiyor; sonuc
zara birakilmiyor. Uretim davranisi degismedi.

**07R-07U kapanisi:** tam suite **2.650 basarili / 0 basarisiz** (+17). Dort duzeltme de
gecici olarak kapatilarak 13 testin eski davranisi yakaladigi kanitlandi (ilk kapatma
denemem operator onceligi yuzunden ATTR filtresinin yalnizca ilk kosulunu kapatmisti;
duzeltilip tekrarlandi).

**Acik kalan:** 07T raporunun onerdigi tam canta / ikinci kez equip / gercek ITEMDEF
CAN_I_PILE varyantlari; `drop all` icin summoned+ATTR birlesimleri ve saci/sakali
olan pet; equip taramasinin referanstaki "en iyi silahi sec" puanlamasi
(NPC_GetWeaponUseScore) - SphereNet hala canta sirasina gore ilk uyani aliyor.

### 07V-07X - oyuncu kusanma yasam dongusu: obur el, surukleme sonu, ret yolu (6 Eylul 2026)

Kanit raporlari: [07V](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07V_OYUNCU_KILIC_KALKAN.md),
[07W](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07W_KUSANMA_SONRASI_SURUKLEME.md),
[07X](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07X_KUSANMA_MACRO_RET.md).
Uc bulgu da bagimsiz olarak dogrulandi. 07X'in kendi uyarisi ("basari ve ret yollari
07W ile birlikte ele alinmali") dogru cikti: ucu de tek bir tamamlama noktasinda
birlestirildi.

- [x] **SX-07V-01 (P2)** - `Item.IsTwoHanded` katmani tek basina yeterli sayiyordu.
  (YAPILDI: once ITEMDEF TWOHANDS, sonra `EquipLayer == TwoHanded && IsWeaponType`.
  Bu, bir onceki turda 07R'yi yazarken pet tarafinda fark edip yalnizca orada
  gecistirdigim tuzagin kaynaktaki karsiligi - raporun da gosterdigi gibi asil yer
  burasiymis; pet tarafindaki yerel geciciye artik gerek yok ama zararsiz oldugu icin
  duruyor.
  **Kapsam denetimi:** `IsTwoHanded`in butun cagrilari gozden gecirildi (NpcAI, combat
  swing gecikmesi, saldiri animasyonu, EquipLastWeapon katmani, `Character.Equip`
  terfisi) - hepsi zaten bir SILAH tasiyor, tek davranis degisikligi kalkanlarda.)
- [x] **SX-07W-01 (P2)** - Basarili `HandleItemEquip` suruklemeyi kapatmiyordu.
  (YAPILDI: `SettleEquipDrag` - referans equip istegini dogrular dogrulamaz surukleme
  modunu kapatir, receive.cpp:542.)
- [x] **SX-07X-01 (P2)** - Reddedilen esya katmansiz/capsiz/suruklemesiz kaliyordu.
  (YAPILDI: ret yolunda `RestoreToOrigin` + surukleme imleci iptali - referansin
  `Event_Item_Drop_Fail`i, CClientEvent.cpp:248.
  **Raporun otesinde kapatilan iki nokta:** (1) `TryDClickEquip` hedef katmani
  kusanmadan ONCE bosaltiyordu, yani reddedilen equip tasiyicinin elini bosaltiyordu -
  referansta guc sarti katman catismasina dokunulmadan once cozulur
  (CCharStatus.cpp:333); katman bosaltma artik kapilarin arkasinda ve paket yolunda da
  calisiyor. (2) `EquipLastWeapon` makrosu silahi cantadan cikarip suruklemesiz
  birakiyor; ret halinde bu esya da ortada kaliyordu - `ItemBounce` karsiligi cantaya
  donuyor.)

**07V-07X kapanisi:** tam suite **2.662 basarili / 0 basarisiz** (+12). Uc duzeltme de
gecici olarak kapatilarak 9 testin eski davranisi yakaladigi kanitlandi.

**Acik kalan:** 07W/07X raporlarinin kuyruklari - script kusanma sirasinda nesneyi
tasirsa/silerse, save/load ile yarim surukleme, gercek cift tiklama girisi, makro
listesinde ilk esya reddedilip sonrakinin kusanilmasi, dolu/silinmis baslangic cantasi.

### 07Y - envanter/esya kullanimi toplu tur: 6 bulgu (6 Eylul 2026)

Kanit raporu: [07Y](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07Y_ENVANTER_ESYA_TOPLU.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **SX-07Y-01 (P2)** - Cikarma makrosu @Unequip'i atliyordu. (YAPILDI: trigger
  makroda da calisiyor.
  **BILINCLI SAPMA - kayit:** referans OnRemoveObj'de reddedemez ("It can not FAIL!"),
  raporun kendisi de bunu veto sozlesmesi saymamak gerektigini yaziyor. Yine de RETURN 1
  burada da ret sayiliyor: SphereNet'in pickup yolu bunu zaten yapiyor ve makroyu
  notification'a birakmak, o reddi asmanin yolu olurdu. Parite turunda geri
  "duzeltilmemeli"; degistirilecekse iki yol birlikte degistirilmeli.)
- [x] **SX-07Y-02 (P2)** - Makas yalnizca tur alanini degistiriyordu. (YAPILDI: cikti
  esyasi yaratiliyor, renk/adet tasiniyor, girdi siliniyor; kumas+giysi -> bandaj,
  hide -> TDATA1 derisi ya da duz deri.
  **Ayrica:** kanli bandaj makas dalindan cikarildi ve referanstaki su yolu eklendi
  (dclick -> hedef -> su). Raporun isaret ettigi mevcut test
  `Scissors_BloodyBandageStack_IsCleanedNotDeleted` uyduruk davranisi kodluyordu;
  yerine uc test geldi (suda temizlenir / kuru hedefte temizlenmez / makas dokunmaz).
  **Kapsam disi:** cloth bolt -> cloth (`ConvertBolttoCloth`) RESOURCES tanimi ister;
  rapor da kanit saymamisti - dokunulmadi.)
- [x] **SX-07Y-03 (P2)** - Aktif anahtar yolu yalniz `TAG.LINK == hedef UID` ariyordu.
  (YAPILDI: `KeyFits` - kilit kodu hedefin kendisi VEYA baglandigi yapi; `HandleKeyUse`
  ve `FindBackpackKeyFor` ayni cozumlemeyi paylasiyor. Test gercek
  `HousingEngine.CreateHouseKey` ile uretilen anahtari kullaniyor.)
- [x] **SX-07Y-04 (P2)** - Kova rengi yerine DYE_HUE etiketi uygulaniyordu. (YAPILDI:
  kovanin kendi Hue'su otorite; eski kayitlardaki etiket yalnizca kovanin kendi rengi
  yokken okunuyor - raporun istedigi "bayat etiket yeni rengi ezmesin" gecis kurali.)
- [x] **SX-07Y-05 (P2)** - Boyamada sahiplik/uygunluk denetimi yoktu. (YAPILDI:
  ust duzey sahip aktor olmali + Clothing/DYE/CAN_I_DYE; GM istisnasi korundu.)
- [x] **SX-07Y-06 (P2)** - @Dye ARGN1 geri okunmuyordu. (YAPILDI: TriggerArgs yerelde
  tutuluyor, N1 renge yaziliyor. **Ek:** referansin ARGN2 ses sozlesmesi de kuruldu -
  0x23E ile beslenip script degistirebiliyor; rapor bunu olcmemisti.)

**Mevcut testlerin duzeltilmesi:** raporun isaret ettigi iki beklenti referansa aykiriydi
ve degistirildi - makasla kanli bandaj temizleme (yukarida) ve
`GameClient_DyeVatApply_FiresDyeTriggerAndCanApplyHue`, ki normal oyuncunun YERDEKI genel
bir nesneyi boyamasini bekliyordu. Ikincisi artik oyuncunun kendi cantasindaki giysiyi
ve kovanin kendi rengini kullaniyor; @Dye tetiklenmesi iddiasi korundu.

**07Y kapanisi:** tam suite **2.684 basarili / 0 basarisiz** (+22). Alti duzeltme de
gecici olarak kapatilarak testlerin eski davranisi yakaladigi kanitlandi: birlesik
kapatmada 16 kirmizi, ardindan yalniz 07Y-05 kapatilarak uc boyama-hedefi testi ayrica
dogrulandi (birlesik kapatmada kova rengi de sifirlandigi icin o ucu gizliyordu).

**Acik kalan:** cloth bolt donusumu; kova renk secici paketi (0x95) ve save/load; sac
boyasinin dialog/harcama akisi; keyring icindeki anahtarlar ve legacy MORE/lock-code
import'u; @Unequip'in bir makroda birden fazla katman ve nesne silme/tasima varyantlari.

### 07Z - tarim, kovan ve su: 6 bulgu (6 Eylul 2026)

Kanit raporu: [07Z](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_07Z_TARIM_KAYNAK_TOPLU.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **SX-07Z-01 (P2)** - Ekimde toprak/agac/mevcut bitki kurallari yoktu. (YAPILDI:
  `HasSoilAt` + agac reddi + mevcut cropun DEGISTIRILMESI; GM istisnasi korundu.
  **Uyarlama notu:** referans toprak aramasini script paketinin TILETYPE tablosundan
  yapar (grass genelde t_dirt DEGILDIR; pakette t_dirt 0x3573 ailesi statiktir).
  SphereNet'te bu tablo yok, bu yuzden sirayla: karedeki dinamik `t_dirt` esya ->
  statiklerin ITEMDEF turu -> `ObjBase.ClassifyTerrainType` (P.TYPE ile ayni kaynak).
  `ClassifyTerrainType` bu yuzden internal yapildi.)
- [x] **SX-07Z-02 (P2)** - TDATA2 buyume asamasi denetlenmiyordu. (YAPILDI: grow id
  sifir degilse "olgun degil" - urun de yok, reset de yok.)
- [x] **SX-07Z-03 (P2)** - @ResourceTest/@ResourceGather hic calismiyordu. (YAPILDI:
  iki asama, referans sirasi ve read-back ile; ResourceGather vetosunda urun siliniyor
  ve crop reset EDILMIYOR.
  **Kapatilamayan yari, kayda gecirildi:** referansin `HALFBAKED` donusu (urunu ayaga
  dusurme) SphereNet'in `TriggerResult`'inda yok; o bosluk kapanmadan uygulanamaz.
  **Raporun otesinde:** ARGN3 (ornege ozel meyve gecersiz kilmasi) icin SphereNet'in
  karsiligi zaten vardi - `PlantDropFruit` MORE2'yi okuyor; hasat yolu da artik ayni
  degeri besliyor.)
- [x] **SX-07Z-04 (P2)** - Urun zorla Food yapiliyordu. (YAPILDI: tur tanimdan geliyor.
  **Raporun ayri saymadigi ikinci yol da kapatildi:** `Item.PlantDropFruit` zamanlayici
  yolunda ayni atama vardi; iki yol farkli tur uretmesin diye birlikte duzeltildi. Bu
  nedenle `SourceXWave270Tests` mature-stage testi urunu Food yerine kendi grafigiyle
  sayacak sekilde guncellendi - raporun "bu beklenti de incelenmeli" notu.)
- [x] **SX-07Z-05 (P2)** - Kovan stok/zamanlayici tanimiyordu. (YAPILDI: MORE1 stogu,
  3'lu zar (bal/balmumu/sokma), 15 dk timeout ve tick'te 5'e kadar dolum.
  **Sapma notu:** sokma referansta `OnTakeDamage(rand(5), POISON|GENERAL)`; SphereNet'te
  eskiden `ApplyPoison(1)` vardi - referansin hasar yolu (`ApplyScriptDamage`) alindi.)
- [x] **SX-07Z-06 (P2)** - Surahi hedefteki su nesnesini gormuyordu. (YAPILDI:
  `ResolveWaterTarget` - dinamik su esyasi / statik / arazi; kanli bandaj yolu da ayni
  yardimciyi kullaniyor.)

**07Z kapanisi:** tam suite **2.704 basarili / 0 basarisiz** (+20). Alti duzeltme de
gecici olarak kapatilarak 14 testin eski davranisi yakaladigi kanitlandi; ardindan yalniz
@ResourceTest asamasi kapatilarak iki trigger testi ayrica dogrulandi (birlesik kapatmada
olgunluk denetimi de kapali oldugu icin biri gizleniyordu).

**Acik kalan:** HALFBAKED donusu; gercek statik toprak ve farkli kat/LOS ile ekim;
ResourceTest'in nesneyi silmesi; kovanda balmumu/sokma dallarinin kontrollu RNG ile
dogrulanmasi ve save/load; gercek statik MUL su.

### 08A - eritme ve onarim: 6 bulgu (6 Eylul 2026)

Kanit raporu: [08A](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_08A_ERITME_ONARIM_TOPLU.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **SX-08A-01 (P2)** - Madenin native TDATA1 kulce tanimi okunmuyordu. (YAPILDI:
  oncelik acikca belirlendi - ornek/def TAG.SMELT_TO > ITEMDEF TDATA1 > duz demir;
  raporun "onceligi acik olmali" notu.)
- [x] **SX-08A-02 (P2)** - Basarisiz eritme butun yigini siliyordu. (YAPILDI:
  `ConsumeOreAmount` ile rand(adet/2)+1 kismi kayip + istemci bildirimi.)
- [x] **SX-08A-03 (P2)** - @Smelt sozlesmesi eksikti. (YAPILDI: ARGN1 Mining becerisi,
  ARGN2 kaynak cesit sayisi, ARGN3 minimum-beceri atlatma, LOCAL.resource.0.ID/.amount;
  hepsi geri okunuyor ve verim `adet x perOre` olarak uygulaniyor.
  **Mevcut test guncellendi:** `ItemUseParityTests` @Smelt vetosu ARGN1'i maden ADEDI
  bekliyordu - raporun isaret ettigi yanlis beklenti.)
- [x] **SX-08A-04 (P2)** - @Create birlesmis eski yiginda calisiyordu. (YAPILDI: trigger
  yeni kulcede, teslim/istifleme oncesinde.)
- [x] **SX-08A-05 (P2)** - Onarim ors ve kaynak kosullarini atliyordu. (YAPILDI:
  `HasAnvilNearby` (2 kare; dinamik + statik) ve `CraftingEngine.TryConsumeResourcePart`
  ile test-once/tuket-sonra.
  **Yapisal karar:** kaynak muhasebesi zaten CraftingEngine'de vardi; statik bir engine
  hook'u eklemek yerine `IActiveSkillSink.Crafting` (varsayilani null) ile veriliyor -
  yeni global durum yok, `ResetEngineStatics`'e ekleme gerekmiyor.)
- [x] **SX-08A-06 (P2)** - Onarimda Arms Lore asamasi yoktu. (YAPILDI: uretim
  becerisinden ONCE; basarisizlikta negatif kazanim ve erken cikis, basarida kazanim
  uretim rulosundan hemen once - referans sirasi.
  **Mevcut iki test guncellendi:** ikisi de orssuz/Arms Lore'suz basari bekliyordu.)

**08A kapanisi:** tam suite **2.718 basarili / 0 basarisiz** (+14). Alti duzeltme de
gecici olarak kapatilarak 10 testin eski davranisi yakaladigi kanitlandi.

**Yeni testlerde belirsizligi kaldirma:** onarim/eritme rulolari `Character.OnSkillUseQuick`
ile sabitlendi (raporun kendi yontemi). Bunu once yapmamistim; `ASmithWhoCannotIdentify...`
filtreli kosuda gecip tam suitede kizardi - 0 beceriyle bile can egrisi ara sira basari
veriyor.

**Acik kalan:** cok cesitli kaynak veren esyalarin eritilmesi (RESOURCES listesi >1);
gem ciktisi; kulce SKILLMAKE minimum-beceri araligi; onarimda kismi/coklu kaynak,
statik ors ve ic ice cantalar; skillgain miktarlari.

### 08B - hedefli esya kullanimi: 6 bulgu (6 Eylul 2026)

Kanit raporu: [08B](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_08B_HEDEFLI_KULLANIM_TOPLU.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **08B-1 (P2)** - Balik kesmede yalnizca erisim deneniyordu. (YAPILDI:
  `CanConsumeTarget` = referansin CanUse(hedef, MOVE) karsiligi; donusum de artik
  yerinde - yeni yigin uretmek baskasinin baligini kesenin eline tasiyordu.)
- [x] **08B-2 (P2)** - Kirkim yun yenilenmesini baslatmiyordu. (YAPILDI: `Layer.FlagWool`
  (=46, referansin kendi numarasi) uzerinde sureli isaret esyasi; suresi dolunca beden
  geri donuyor.
  **Yapisal not:** SphereNet'in Layer enum'u 31'de bitiyordu; referansin dahili katman
  bloguna yalnizca ihtiyac duyulan giris eklendi. `Layer.Qty` degistigi icin
  `ItemInventoryParityTests.Layer_MatchesUoLayerTypeNumbering` guncellendi - eski
  `Qty == 32` iddiasi SphereNet'in kesilmis modelini kodluyordu, referansta da 32 degil.
  **Raporun otesinde:** dahili katmanlar artik istemciye gonderilmiyor
  (`SendItemVisualUpdate` > Layer.Horse guard) - referans "don't bother sending these".
  Ayrica bicak dagiticisi kirkilmis koyunu da yanitliyor, yoksa yeni uyari mesaji
  erisilemez kalirdi.)
- [x] **08B-3 (P3)** - Yatak rulosu acilip kapanmiyordu. (YAPILDI: 0A57/0A58/0A59 ->
  acik, 0A55/0A56 -> kapali; cantadaki rulo icin "once yere koy". Tanimadigi grafik
  eski Camping davranisini koruyor.)
- [x] **08B-4 (P2)** - Top doldurmada hedef/mermi dogrulanmiyordu. (YAPILDI: muzzle icin
  erisim, mermi icin `CanConsumeTarget`.)
- [x] **08B-5 (P2)** - Meyve/ham reaktiften tohum dali yoktu. (YAPILDI: `CutSeedFrom` -
  DEFAULTSEED grafigi, IT_SEED turu ve "<ad> seed" adi, yerinde.)
- [x] **08B-6 (P2)** - Cikrik iki saniyelik mesgul duruma gecmiyordu. (YAPILDI:
  `Item.SetAnim`/`EndAnim` - onceki grafik MORE1'de, onceki tur MORE2'de, tur
  `AnimActive`, timer 2 sn; `Item.OnTick` geri donduruyor. `AnimActive` dclick mesaji
  zaten vardi, onu kuran yoktu.)

**08B kapanisi:** tam suite **2.736 basarili / 0 basarisiz** (+18). Alti duzeltme de
gecici olarak kapatilarak 15 testin eski davranisi yakaladigi kanitlandi.

**Kendi testimi duzelttim:** ilk kapatma kosusunda "sabit balik" ve "baskasinin baligi"
testleri yesil kalmisti - eski kod baligi silip filetoyu cantaya koydugu icin
`fish.ItemType` silinmis nesnede hala Fish okunuyordu. Iddia `IsDeleted == false` ve
"cantada fileto yok" olarak guclendirildi.

**Acik kalan:** yun sayisinin script tanimlarina gore degerlendirilmesi ve save/load
sonrasi yenilenme; cikrik anim durumunun kayittan donmesi; yatak rulosunun 1F24-1F27
ailesi; top atisi hasari; DEFAULTSEED'in gercek paketle cozulmesi.

### 08C - antrenman, icki ve iletisim: 6 bulgu (6 Eylul 2026)

Kanit raporu: [08C](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_08C_EGITIM_ICECEK_ILETISIM.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **08C-1 (P2)** - Yankesicilik antrenman esyasi calinabiliyordu. (YAPILDI:
  `TrainOnPickpocketDip` - yer/mesafe/binek/antrenman siniri, rulo ve `SetAnim`; esya
  yerinde kaliyor.
  **Kapsam notu:** referans bunu iki asamali NPCACT_TRAINING isi olarak kurar
  (baslat/bitir); SphereNet'te oyuncu icin o eylem makinesi yok, bu yuzden rulo ve
  animasyon tek adimda - antrenman zamanlamasi acik kaldi.)
- [x] **08C-2 (P2)** - Ok hedefinden muhimmat geri alinamiyordu. (YAPILDI:
  `GatherButteAmmo`, beceriye yonlenmeden once.)
- [x] **08C-3 (P2)** - Yirtilan ag uydurma kaynak birakiyordu. (YAPILDI: yalnizca silme.
  **Mevcut test guncellendi:** `ParityWaveH3Tests.Web_Struggle_DestroysWebLeavesSilk...`
  bu uydurma ciktiyi bekliyordu - raporun isaret ettigi yanlis beklenti.)
- [x] **08C-4 (P2)** - @Drink sozlesmesi yoktu. (YAPILDI: yeni `CharTrigger.Drink`,
  ARGN1/ARGN2/LOCAL.BottleId ve geri okuma; veto icmeyi durduruyor.
  **Kapsam:** referansin ELSEIF/HALFBAKED dallarindaki bos-kap adedi SphereNet'in
  `TriggerResult`'inda karsiliksiz - acik kaldi.)
- [x] **08C-5 (P2)** - Alkol sarhosluk etkisi baslatmiyordu. (YAPILDI: `SpellType.Liquor`
  dogrudan etkisi, gucu rand(300)+10.)
- [x] **08C-6 (P2)** - Kristal genel hedef imleci kullaniyordu. (YAPILDI:
  `SetPendingItemTarget` - kaynak dogrulamasi ve @TargOn_Item korumasi devreye giriyor.)

**08C kapanisi:** tam suite **2.749 basarili / 0 basarisiz** (+13). Alti duzeltme de
gecici olarak kapatilarak 9 testin (+1 guncellenen mevcut testin) eski davranisi
yakaladigi kanitlandi.

**Kendi testimi duzelttim:** ilk kapatma kosusunda yankesicilik testleri yesil kalmisti -
tezgahta `SkillHandlers` bagli olmadigi icin eski yol zaten hicbir sey yapmiyordu.
Tezgaha gercek beceri hattini bagladim; ancak o zaman eski davranis (esyanin cantaya
gitmesi) testleri kirmiziya cevirdi.

**Acik kalan:** antrenman isinin zamanlamasi ve tamamlanma kazanimi; @Drink'in bos-kap
uretimi; kristalin kendi kendine baglanmasi ve hedef turu kurallari.

### 08D - tuketim, antrenman ve hasar girisleri: 6 bulgu (6 Eylul 2026)

Kanit raporu: [08D](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_08D_TUKETIM_EGITIM_HASAR.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **08D-1 (P2)** - Tok oyuncuda yemek yine tuketiliyordu. (YAPILDI: `EatOneUnit` -
  motorun dondurdugu adet uzerinden tuketim; 0 ise mesaj ve tuketim yok.)
- [x] **08D-2 (P2)** - Tahil/ot son adette tukenmiyor, sabit hedef de doyuruyordu.
  (YAPILDI: Grain/Grass ayni yemek yoluna alindi + CanMove kapisi.
  **Mevcut test guncellendi:** `GrainStackDoubleClick_DecrementsButNeverDeletesLastUnit`
  son adedin korunmasini bekliyordu - raporun isaret ettigi uydurma politika; yerine
  "sonuna kadar yenir" ve "sabit kaynak kimseyi doyurmaz" testleri geldi.
  **Kapsam:** WaterWash referansta Use_Drink'tir, ayri sozlesme - eski yolunda birakildi.)
- [x] **08D-3 (P2)** - Bos oyun tahtasi taslarini olusturmuyordu. (YAPILDI:
  `SetUpGameBoard` - satranc/dama/tavla dizilimleri ve kap koordinatlari; dolu tahta
  korunuyor.)
- [x] **08D-4 (P2)** - Dovus mankeni antrenman islemi baslatmiyordu. (YAPILDI:
  `TrainOnDummy` + `ResolveWeaponSkill` - mesafe/binek/menzil/antrenman siniri, `SetAnim`
  ve silahin kendi becerisinde kazanim.
  **Kapsam notu:** 08C'deki gibi iki asamali NPCACT_TRAINING zamanlamasi modellenmedi.)
- [x] **08D-5 (P2)** - Tuzak hasari @GetHit sozlesmesini atliyordu. (YAPILDI:
  `CombatEngine.ApplyScriptDamage` ile kunt/genel hasar.)
- [x] **08D-6 (P2)** - Tapinak diriltmesi @SpellEffect calistirmiyordu. (YAPILDI: veto
  diriltmeyi durduruyor; @Resurrect zaten calisiyordu.)

**08D kapanisi:** tam suite **2.769 basarili / 0 basarisiz** (+20). Alti duzeltme de
gecici olarak kapatilarak 13 testin eski davranisi yakaladigi kanitlandi.

**Kendi testimi duzelttim:** manken testleri once `OnSkillGain` sayiyordu; kazanim
sansa bagli oldugu icin bu iki yolu ayirt etmiyordu. Olcut mankenin kendi animasyon
durumu yapildi - vurus varsa uc saniyelik AnimActive, ret varsa bos.

**Acik kalan:** WaterWash icme yolu; oyun taslarinin istemcide dizilimi ve tahta
kayit/yukleme; manken/dip antrenman zamanlamasi; tuzak hasarinda zirh/direnc ve olum
sirasi.

### 09A - sohbet kanallari ve moderasyon: 6 bulgu (6 Eylul 2026)

Kanit raporu: [09A](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_09A_SOHBET_KANALLARI.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **09A-1 (P2)** - Basarisiz gecis eski kanali kaybettiriyordu. (YAPILDI: once hedef
  denetleniyor, sonra eski uyelik birakiliyor ve ayrilma bildirimi uretiliyor.)
- [x] **09A-2 (P2)** - Katilma komutunda parola okunmuyordu. (YAPILDI: `ParseJoinCommand`
  ("Ad" parola) ve `ParseCreateCommand` (Ad{parola}) ayrildi.)
- [x] **09A-3 (P2)** - DefaultVoice degisimi mevcut uyeleri susturuyordu. (YAPILDI: ses
  uyeye katilirken veriliyor; `CanSpeak` yalnizca uyenin kendi kaydini soruyor.
  **Mevcut test guncellendi:** `ChatSystemTests.ChatEngine_ModeratedChannel_...` eski
  uyenin ayarla sessizlesmesini bekliyordu - raporun isaret ettigi beklenti; yerine
  "eski uye konusmaya devam eder / sonradan gelen sessiz baslar / bireysel susturma
  calisir" geldi.)
- [x] **09A-4 (P2)** - Kurucunun moderatorlugu kaldirilamiyordu. (YAPILDI: `SeatFounder`
  ile kurucu da normal moderator kaydi aliyor; `IsModerator` yalnizca listeye bakiyor.)
- [x] **09A-5 (P2)** - Olusturma ve katilma tek isleme dusmustu. (YAPILDI: `Join(...,
  create)` - olusturmada ad bos olmali, katilmada kanal var olmali.
  **Sozlesme degisikligi:** `ChatEngine.Join` artik otomatik kanal yaratmiyor; mevcut
  motor testlerinin ad-hoc kanal kuran cagrilarina `create: true` eklendi.)
- [x] **09A-6 (P2)** - Kanal listesi degisimleri duyurulmuyordu. (YAPILDI:
  `AnnounceToChat` - olusturma, bosalan kanalin kaldirilmasi ve yeniden adlandirmada
  global kaldir/ekle. Motor `Participants` ve `Exists` verecek sekilde genisletildi;
  `Rename` eski adi geri donduruyor.)

**09A kapanisi:** tam suite **2.780 basarili / 0 basarisiz** (+11). Alti duzeltme de
gecici olarak kapatilarak 9 testin eski davranisi yakaladigi kanitlandi.

**Acik kalan:** kanal silme ve parola degisiminin ilani; raporun isaret ettigi referans
ici tutarsizlik (`RemoveVoice(true)`); parolanin uc durumlari; sohbet penceresi yeniden
acildiginda liste tazeligi.

### 09B - parti davetleri ve uyelik yasam dongusu: 6 bulgu (6 Eylul 2026)

Kanit raporu: [09B](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_09B_PARTI_DAVET_UYELIK.md).
Alti bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **09B-1 (P2)** - Kabul/ret yanitindaki davetci kimligi yok sayiliyordu. (YAPILDI:
  paketteki UID bekleyen davetle eslestiriliyor; uyusmazlikta bekleyen davet tuketilmiyor.
  Ret yolu da ayni eslestirmeyi yapiyor.)
- [x] **09B-2 (P2)** - Kabulde gorunurluk yeniden denetlenmiyordu. (YAPILDI:
  `CanStillSee` - ayni harita, gorus mesafesi ve gizlenme kurallari.)
- [x] **09B-3 (P2)** - PARTY_AUTODECLINEINVITE okunmuyordu. (YAPILDI: her iki davet
  girisinde de - protokol Add ve `HandlePartyInvite`.)
- [x] **09B-4 (P2)** - Katilimda @PartyAdd yoktu. (YAPILDI: yeni `CharTrigger.PartyAdd`,
  uyelik degismeden once; veto katilimi durduruyor.)
- [x] **09B-5 (P2)** - @PartyRemove vetosu yok sayiliyor, @PartyLeave hic calismiyordu.
  (YAPILDI: iki asama da cikarmadan ONCE ve ikisi de reddedebiliyor.)
- [x] **09B-6 (P2)** - Liderin protokolle ayrilmasi partiyi dagitmiyordu. (YAPILDI:
  lider kendisini cikardiginda `Disband`.
  **Ayrim korundu:** `PartyManager.Leave`'in lider devri davranisi kaldirilmadi -
  referansta fDisband=false ile desteklenen ayri bir yol; `Party_LeavePromotesNewMaster`
  testi bu yuzden gecerli kaliyor ve degistirilmedi.)

**09B kapanisi:** tam suite **2.793 basarili / 0 basarisiz** (+13). Alti duzeltme de
gecici olarak kapatilarak 10 testin eski davranisi yakaladigi kanitlandi.

**Acik kalan:** davet hiz siniri; baglanti kesilmesi (09C'de); parti sohbet filtresi;
dagitma vetosunun sirasi (09C-1).

### 09C-09D - parti/lonca tutarliligi ve lonca kayitlari: 11 bulgu (6 Eylul 2026)

Kanit raporlari: [09C](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_09C_PARTI_LONCA_TUTARLILIK.md),
[09D](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_09D_LONCA_KAYIT_ILISKILER.md).
Onbir bulgu da bagimsiz olarak dogrulandi; ikisi ayni alt sistemde oldugu icin tek turda
uygulandi.

- [x] **09C-1 (P2)** - PartyDisband vetosu dagitmadan SONRA calisiyordu. (YAPILDI: dagitma
  oncesinde, partiyi elinde tutan uyede; veto dagitmayi durduruyor.
  **Mevcut test guncellendi:** `PetPartyTriggerTests.PartyDisband_RemovingLastMember_...`
  her eski uyede tetiklenmesini bekliyordu.)
- [x] **09C-2 (P2)** - Baglanti kesilmesi parti guncellemesi gondermiyordu. (YAPILDI:
  `LeavePartyOnDisconnect` - kalanlar yeni listeyi ve waypoint kaldirmayi aliyor.)
- [x] **09C-3 (P2)** - Script ekleme uyelik butunlugunu korumuyordu. (YAPILDI: iki komut
  da UID'yi karaktere cozuyor; siradan ekleme baska partideki kisiyi reddediyor.)
- [x] **09C-4 (P2)** - Lider degisince MEMBER.0 eski lideri gosteriyordu. (YAPILDI:
  `SetMaster` lideri listenin basina tasiyor.)
- [x] **09C-5 (P2)** - MEMBER.COUNT alt sinir gibi yorumlaniyordu. (YAPILDI: tam rol
  esitligi; parametresiz sorgu hepsini sayiyor.)
- [x] **09C-6 (P2)** - Uye ayrilinca secim yapilmiyordu. (YAPILDI: `GuildManager.MemberLeft`
  - cikarma + `ElectMaster`; lonca gump'inin Leave dali oradan geciyor.)
- [x] **09D-1 (P2)** - Dagitilan lonca eski etiketlerden geri geliyordu. (YAPILDI:
  `RemoveGuild(stoneUid, world)` tasin GUILD.* etiketlerini de temizliyor.)
- [x] **09D-2 (P2)** - Silinen tasin loncasi manager'da yasiyordu. (YAPILDI:
  `OnStoneDeleted`, sunucunun `ObjectDeleting` kancasina baglandi.)
- [x] **09D-3 (P2)** - Uyesiz lonca savasini surduruyordu. (YAPILDI: son tam uye
  ayrilinca ilisikiler iki yonde de temizleniyor - manager iki tarafi da gordugu icin bu
  is `MemberLeft`'te.)
- [x] **09D-4 (P2)** - Unvandaki gercek ters cizgi kayboluyordu. (YAPILDI: kacis
  karakteri de kacisliyor (`\\e`); eski kayitlardaki taninmayan ciftler oldugu gibi
  korunuyor.)
- [x] **09D-5 (P2)** - ABBREV/CHARTER yuklemede sessizce kirpiliyordu. (YAPILDI: okuma
  tarafindaki kirpma kaldirildi - sinir gerekiyorsa yazma tarafinda olmali.)

**09C-09D kapanisi:** tam suite **2.811 basarili / 0 basarisiz** (+18). Onbir duzeltme de
gecici olarak kapatilarak 15 testin eski davranisi yakaladigi kanitlandi.

**Kendi testimi duzelttim:** script ekleme testleri once `TrySetProperty` cagiriyordu;
PARTY.* bir KOMUT (`TryExecuteCommand`), dolayisiyla hicbir sey calismiyor ve iki olumsuz
iddia bosuna geciyordu. Dogru giris noktasina cevrildi ve komutun true dondugu de
iddia edildi.

**Acik kalan:** parti sohbet filtresi ve davet hiz siniri; lonca savas/baris gump akisi;
JoinTime alani; town tasi varyantlari; kirpma sinirlarinin yazma tarafinda uygulanmasi.

### 10A-10B - gemi guvertesi, donus, plank, pilot ve redeed: 10 bulgu (6 Eylul 2026)

Kanit raporlari: [10A](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_10A_GEMI_GUVERTE_DONUS.md),
[10B](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_10B_PLANK_PILOT_REDEED.md).
On bulgu da bagimsiz olarak dogrulandi; ikisi ayni alt sistemde oldugu icin tek turda
uygulandi.

- [x] **10A-1 (P2)** - ATTR_STATIC esyalar gemiyle tasiniyordu. (YAPILDI: `RidesNowhere`
  yalnizca Static'i disliyor; MOVE_NEVER tasinmaya devam ediyor - raporun ozellikle
  uyardigi ayrim.)
- [x] **10A-2 (P2)** - Yukseklik govde tabanina gore olculuyordu. (YAPILDI: guverte
  duzlemi `shipZ + max(3, DefHeight)`, pencere `-2..PLAYER_HEIGHT`.
  **Mevcut uc test guncellendi:** `ShipHousingParityTests` ve `HousingShipIntegrityTests`
  yolcu/yuku govdenin kendi Z'sine - yani guvertenin altina - koyuyordu; eski gevsek
  pencere bunlari kabul ediyordu. Fixture'lar guverteye tasindi.)
- [x] **10A-3 (P2)** - Asimetrik govdede uzak guverte arama yaricapinin disinda
  kaliyordu. (YAPILDI: `DeckSearchRange` - anchor'a en uzak kenar; uc ayri formul tek
  yardimciya indi.)
- [x] **10A-4 (P2)** - @Ship_Turn yalnizca govdede ve argumansiz calisiyordu. (YAPILDI:
  hook `(Item, newDir, oldDir)` oldu; govde + bilesenler + serbest yuk.
  **Mevcut test guncellendi:** `ShipRedeedTriggerTests` tek argumanli hook'u sayiyordu.)
- [x] **10A-5 (P3)** - Buyulu gemi dikey guvenlik payini asabiliyordu. (YAPILDI:
  `MoveDelta`'da yonlu yukseklik siniri, hareketten once.)
- [x] **10B-1 (P2)** - Acik plank karari koordinat karsilastirmasiydi, kilitli kapatma
  denetimi yoktu. (YAPILDI: gemi region'i uzerinden ic/dis ayrimi + kilitli tarafta
  anahtar denetimi.)
- [x] **10B-2 (P2)** - Planktan binis Reveal yapmiyordu. (YAPILDI: binis sonrasi
  Hidden/Invisible temizleniyor - referansin teleport sozlesmesi.
  **Kapsam notu:** referansin Reveal'i script vetosu gibi istisnalara acik; SphereNet'te
  o hook yok, bayraklar dogrudan temizleniyor.)
- [x] **10B-3 (P2)** - Donuste acik plank kapali grafik aliyordu. (YAPILDI: acik plank
  yeni tarafin acik grafigini aliyor, More1 yeni tarafin kapali grafigini tutuyor.)
- [x] **10B-4 (P3)** - Pilot atamasi bagli esya olusturmuyordu. (YAPILDI: LAYER_HORSE
  uzerinde gemiye bagli pilot esyasi; dumen birakilinca siliniyor.)
- [x] **10B-5 (P2)** - Gemi redeed akisinda @Redeed yoktu. (YAPILDI: `OnShipRedeed`
  hook'u ve sunucu baglantisi; eski multi, ARGO deed, ARGN1 deed grafigi.
  **Sinir korundu:** raporun belirttigi gibi referansta bu bir veto degil - RETURN 1
  gemiyi kurtarmiyor.)

**10A-10B kapanisi:** tam suite **2.833 basarili / 0 basarisiz** (+22). On duzeltme de
gecici olarak kapatilarak 15 testin eski davranisi yakaladigi kanitlandi.

**Acik kalan:** gercek harita/carpisma ile binis; plank otomatik kapanma ve kayit/yukleme;
pilot devri ve HS istemci gorunumu; redeed'in yolcu/sabit yuk varyantlari.

### 10C-10D - gemi kaydi, sahiplik, paket ve donus/komut denetimi: 10 bulgu (6 Eylul 2026)

Kanit raporlari: [10C](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_10C_GEMI_KAYIT_SAHIPLIK_PAKET.md),
[10D](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_10D_GEMI_BOLGE_DONUS_KOMUT.md).
On bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **10C-1 (P2)** - Klasik save'in duz OWNER alani okunmuyordu. (YAPILDI:
  DeserializeFromWorld SHIP.OWNER'a ek olarak OWNER etiketini de kabul ediyor.)
- [x] **10C-2 (P2)** - Canli gemide OWNER yazmak sahipligi degistirmiyordu. (YAPILDI:
  yazma kayitli gemiyi bulup Ship.Owner'i tasiyor; etiket yine round-trip ediyor.)
- [x] **10C-3 (P2)** - Silinmis plank listede kaliyordu. (YAPILDI: PruneDeletedPlanks;
  GetPlank ve yeni GetPlankCount(world) once listeyi tazeliyor.)
- [x] **10C-4 (P2)** - @Ship_Move kare basina ve yonsuz tetikleniyordu. (YAPILDI: olay
  tamamlanan hareket KOMUTUNA tasindi, ARGN1 = yon.
  **Rapordan sapma - referans yeniden okundu:** rapor ARGN2'yi "durdu mu" olarak
  tarif ediyor; Source-X yarida kalan emirde trigger'dan BIR DAL ONCE doner (:851), bu
  yuzden tetiklendigi noktada fStopped kanitlanabilir sekilde 0'dir. Hook tek yon
  argumaniyla birakildi, ARGN2 sabit 0 ve engellenen gemi hic olay bildirmiyor.)
- [x] **10C-5 (P3)** - Negatif govde Z'si pakette sifira kirpiliyordu. (YAPILDI: isaretli
  16-bit deger. **Kapsam notu:** duzeltme Program.EngineWiring'de, test projesinden
  erisilemiyor - regresyon testi yok.)
- [x] **10D-1 (P2)** - MULTIDEF REGIONFLAGS region'a tasinmiyordu. (YAPILDI:
  MultiDef.RegionFlags + MergeScriptMetadata; gemi region'unun ilk kurulusu ve her
  guncellemesi bu tabandan basliyor. **Kapsam genisletmesi:** referansin
  MultiRealizeRegion'i ayni bayraklari EVE de uygular, ayni kok neden - ev region'u da
  duzeltildi. **Acik birakildi:** referansta gemi/ev dali cevre bolgeden bayrak MIRAS
  ALMAZ; SphereNet InheritParentFlags'i koruyor - raporun da ayrica uzlastirilmasini
  istedigi bilincli fark.)
- [x] **10D-2 (P2)** - Donus, zaten kaplanan alandaki engellerce reddediliyordu.
  (YAPILDI: CanPlaceShip'e "su an tutulan ayak izi" parametresi; Face eski tanimi
  veriyor, yalnizca yeni istenen alan denetleniyor.)
- [x] **10D-3 (P2)** - Govde disindaki komsu esya donusu engelliyordu. (YAPILDI: esya
  dongusu 0 yaricap + hucre koordinat denetimi - karakter dongusundeki guardin ayni.)
- [x] **10D-4 (P3)** - SHIPGATE'e referansta olmayan su sarti eklenmisti. (YAPILDI: su
  denetimi kaldirildi; koordinat gecerliligi ve MoveDelta'nin region vetosu duruyor.
  Normal seyrin su sarti test ile ayrica sabitlendi.)
- [x] **10D-5 (P3)** - SHIPFACE caprazi yuvarliyordu. (YAPILDI: Face yalnizca dort govde
  yonunu kabul ediyor. DirFace zaten hem yerlestirmede hem yuklemede 4 yone
  normalize edildigi icin ic donus cagrilari etkilenmiyor; sekiz yonlu SHIPMOVE test
  ile korundu.)

**10C-10D kapanisi:** tam suite **2.856 basarili / 0 basarisiz** (+23). Dokuz duzeltme
gecici olarak kapatilarak 15 testin eski davranisi yakaladigi kanitlandi (10C-5 test
edilemiyor).

**Acik kalan:** gemi/ev region'unun cevreden bayrak mirasi ile Source-X dalinin
uzlastirilmasi; SHIPGATE'in yolcu ve region @Enter akislari; kirik plank listesinin
kayit/yukleme varyantlari.

### 11A-11B - kitap penceresi, harita/pano erisimi ve kapasite: 11 bulgu (6 Eylul 2026)

Kanit raporlari: [11A](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_11A_KITAP_HARITA.md),
[11B](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_11B_PANO_HARITA_DURUM.md).
On bir bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **11A-1 (P1)** - 0x93 yanlis uzunluk ve alan duzeni. (YAPILDI: sabit 99 bayt,
  60/30 sabit genislikte metin alanlari, yazilabilir bayragi iki kez.
  **Rapor otesi:** GELEN 0x93 isleyicisi zaten dogru sabit duzeni okuyordu - iki uc
  birbiriyle de celisiyordu; ClassicUO `OpenBook` da 0x93 icin uzunluk alani aramiyor,
  bu da referansi ikinci kaynaktan dogruluyor. 0x66 ayrica karsilastirildi, uyumlu.)
- [x] **11A-2 (P2)** - Kitap duzenleme girisleri erisim/tur dogrulamiyordu. (YAPILDI:
  sayfa yolunda CanSee + gercek kitap turu + yazilabilirlik, baslik yolunda CanTouch -
  raporun "ikisini tek mesafe kontrolune indirgeme" uyarisina uyuldu.)
- [x] **11A-3 (P2)** - Harita pin degisiklikleri uzak/harita olmayan esyaya
  uygulaniyordu. (YAPILDI: ortak giriste tur + CanTouch.)
- [x] **11A-4 (P2)** - BODY sayfalari bir sayfa kaymis sakleniyordu. (YAPILDI: BODY
  sifir tabanli, depolama bir tabanli, ikisi arasindaki cevrim tek yerde.
  **Rapor otesi:** yazma tarafi kadar OKUMA tarafi da kaymisti - `BODY.n` dogrudan
  `PAGE_n` okuyordu. **Gecis:** `PAGE_0` tasiyan kitap ilk okuma/yazmada yerine
  kaldiriliyor - `PAGE_0` baska hicbir yoldan okunamadigi icin guvenli ve idempotent.)
- [x] **11A-5 (P2)** - Klasik kayittaki tekrarlanan PIN satirlari yuklenmiyordu.
  (YAPILDI: indekssiz `PIN` yazimi sirayla ekliyor.)
- [x] **11A-6 (P3)** - TITLE ile NAME kopuyordu. (YAPILDI: TITLE yazimi adi da kuruyor,
  istemci baslik degisimi de. **Oncelik acikca belirlendi:** OKUMADA hala
  `BOOK_TITLE` tag'i once geliyor, boylece adi hic aynalanmamis eski kayit basligini
  kaybetmiyor - raporun istedigi aktarim onceligi karari.)
- [x] **11B-1 (P2)** - Panodan uzaklasinca okuma/silme suruyordu. (YAPILDI: ortak
  `ResolveBoardMessage` girisinde CanSee; mevcut yazar denetimi korundu.)
- [x] **11B-2 (P3)** - Yeni pano mesajina Move_Never verilmiyordu. (YAPILDI.)
- [x] **11B-3 (P2)** - MOREZ ile sabitlenmis pinler oyuncuya aciti. (YAPILDI: mesaj
  herkese, engel yalnizca GM altina.)
- [x] **11B-4 (P2)** - Harita acilisinda sunucu cizim modu sifirlanmiyordu. (YAPILDI:
  acilis PLOTMODE'u da temizliyor.)
- [x] **11B-5 (P2)** - Acilis 16 sayfa sunarken 64 kabul ediliyordu. (YAPILDI:
  yazilabilir -> 64, bitmis -> gercek sayfa sayisi, yazma tavani 64.
  **Bilincli ayrim:** `BOOK_PAGES` sistem kitabinin resource sayfa sayisi yerine
  geciyor ve referansta orada MAX_BOOK_PAGES kirpmasi yok; oradaki 256 tavani parite
  degil, E1 anti-hang korumasi - iki sabit ayri tutuldu.)

**11A-11B kapanisi:** tam suite **2.881 basarili / 0 basarisiz** (+25). On bir duzeltme
de gecici olarak kapatilarak 20 testin eski davranisi yakaladigi kanitlandi.

**Mevcut testler yeniden kuruldu:** `BookAndPacketLengthTests` iki testi eski
sozlesmeyi kodluyordu - 0x93 sayfa alani degisken paketin 9. ofsetinden sabit paketin
7. ofsetine tasindi, ve gelen satir-kirpma fixture'i tur/erisim kapisi olmadan
calisiyordu (turu olmayan esya, yerlestirilmemis karakter); ikisi de gercek kitaba ve
yakin okura tasindi. Wire-uzunlugu korumalari (T1/E1) aynen duruyor.

**Acik kalan:** sabitlenmis pinlerin kayit/yukleme yolu; pano mesajlarinin @Create ve
decay davranisi; sistem kitabi (RES_BOOK) icerik yolu; 0xD4 yeni baslik paketi hic
uretilmiyor - eski istemci disi surumlerde ayrica degerlendirilmeli.

### 11C-12A - icerik aktarimi ve cevre durumu: 10 bulgu + 1 tur ici bulus (6 Eylul 2026)

Kanit raporlari: [11C](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_11C_ICERIK_PAKET_YUKLEME.md),
[12A](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12A_HAVA_MEVSIM_ISIK.md).
On bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **11C-1 (P2)** - Eski kayitli pano mesajinin govdesi bos gidiyordu. (YAPILDI: pano
  mesaji artik kitapla AYNI sayfa deposunu yaziyor - referanstaki tek CItemMessage sayfa
  listesi - okuma tarafinda onceki SphereNet kayitlarinin BODY_n tag'leri once, sonra
  PAGE_n. **Mevcut test guncellendi:** `ParityWaveH10Tests` depolama tag adini
  dogruluyordu; test artik hem depoyu hem script BODY yuzeyini kontrol ediyor.)
- [x] **11C-2 (P2)** - Yeni istemciye harita facet'i gonderilmiyordu. (YAPILDI: 0xF5, 21
  bayt, son alan facet; surum esigi 7.0.13.0 veya enhanced/KR, altindakiler 0x90.
  **Tuzak:** ilk denemede `FallbackVersionAtLeast` kullandim - o yardimci yalnizca
  BILINMEYEN surum icin cevap veriyor, bilinen surumde daima false; dogrudan sayisal
  karsilastirmaya cevrildi.)
- [x] **11C-3 (P3)** - Ozel harita pencere boyutlari yok sayiliyordu. (YAPILDI: modern
  pakette OVERRIDE.MAPWIDTH/MAPHEIGHT, alan sirasi yukseklik-sonra-genislik; eski paket
  referanstaki gibi override okumuyor.)
- [x] **11C-4 (P2)** - 65 satirlik sayfa sonraki sayfanin sinirini bozuyordu. (YAPILDI:
  bildirilen her satir tuketiliyor, saklama sinirini asanlar atiliyor; 100'un ustunde
  bildiren sayfa istegi bitiriyor - referansin kendi siniri.)
- [x] **11C-5 (P3)** - Salt okunur kitap yazilabilir ilan ediliyordu. (YAPILDI: acilis ve
  duzenleme ortak `IsBookWritableFor` hesabini kullaniyor.)
- [x] **12A-1 (P3)** - DRY/RAIN kodlari uyusmuyordu. (YAPILDI: sektor artik WEATHER_TYPE
  kullaniyor. **Kayit uyumu:** yeni olcek `ENV2` anahtariyla yaziliyor, eski `ENV`
  satiri eski anlamiyla (0=kuru, 1=yagmur, 2=kar) okunup cevriliyor - raporun "eski 0
  degerlerini korlemesine yagmur diye yorumlamamak gerekir" uyarisi.
  **Bilincli sapma:** referansin isik hesabi ham bayta truthy bakar, ki WEATHER_RAIN==0
  oldugu icin yagmuru acik hava sayar; buradaki kontrol "hava var mi" sorusunu soruyor,
  boylece kod degisimi gokyuzunu ters cevirmiyor.)
- [x] **12A-2 (P2)** - Sektor hava/mevsim ayari yayinlanmiyordu. (YAPILDI: setter'lar
  sektordeki HER karaktere bildiriyor - referans paketi yalnizca istemcisi olana, ama
  @EnvironChange'i hepsine gonderir - sunucu tarafi paketi kime yollayacagina kendi
  karar veriyor.)
- [x] **12A-3 (P2)** - Elle verilen isik etkin hesapta korunmuyordu. (YAPILDI:
  LIGHT_OVERRIDE biti, `IsLightOverridden`, `ClearLightOverride`, yeni
  `AllowLightOverride` ini anahtari. Kayitta override durumu ENV2'de acikca tasiniyor;
  eski ENV satirinda sifir olmayan isik override sayiliyor.)
- [x] **12A-4 (P2)** - RAINCHANCE/COLDCHANCE kullanilmiyordu. (YAPILDI: uretim bolgenin
  altindaki sektorun iklimini okuyor; once yagis zari, sonra kar zari.)
- [x] **12A-5 (P2)** - Ayni adli bolgeler ayni havayi paylasiyordu. (YAPILDI: anahtar
  (map, region uid); `OnWeatherChanged` bolgenin kendisini tasiyor ve yayin
  dinleyicilerini kimlikle seciyor - raporun "yalniz sozlugu degistirmek yetmez"
  uyarisi.)

**Tur ici bulus (raporlarda yok):** harita gump'i (0x90/0xF5) Low, ardindan gelen pin
paketleri (0x56) Normal onceliktedi; kuyruk Highest->Low bosaldigi icin pencere kendi
pinlerinden SONRA gidiyordu - satici 0x74 fiyat listesiyle ayni sinif siralama hatasi.
Harita paketleri pinleriyle ayni kuyruga alindi ve sira testle sabitlendi.

**11C-12A kapanisi:** tam suite **2.909 basarili / 0 basarisiz** (+28). On bir duzeltme
de gecici olarak kapatilarak 21 testin eski davranisi yakaladigi kanitlandi.

**Mevcut testler yeniden kuruldu:** `SourceXSectorMoonLightWave234Tests` fixture'i
`Weather = 0` yazarak "kuru" demek istiyordu - yeni olcekte 0 yagmur; `Sector.WeatherDry`
oldu. `ParityWaveH10Tests` pano govdesinin depolama tag adini dogruluyordu.

**Acik kalan:** mevsimin sektor override'i ile global dongunun uzlastirilmasi (su an
override yalnizca bildirimde kullaniliyor); sektor iklimi icin Source-X'in enlem tabanli
SetDefaultWeatherChance dagilimi; WEATHER_CLOUDY (3) uretilmiyor; 0xD4 yeni kitap
basligi hala uretilmiyor.

### 12B-12D - cevre gonderimi, dunya saati ve Champion: 15 bulgu (6 Eylul 2026)

Kanit raporlari: [12B](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12B_CEVRE_GIRIS_HARITA_SORGU.md),
[12C](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12C_DUNYA_SAATI_CHAMPION.md),
[12D](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12D_CHAMPION_BOSS_KAYIT_TEMIZLIK.md).
On bes bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **12B-1 (P2)** - Giris/resync havayi gondermiyordu. (YAPILDI: `GameWorld.ResolveWeather`
  hook'u ve ortak `SendCurrentWeather`; sunucu bunu hava motoruna bagliyor.)
- [x] **12B-2 (P2)** - Bolgesiz alana cikis eski havayi temizlemiyordu. (YAPILDI: erken
  cikis kaldirildi, cevre yayini her zaman yapiliyor, yalnizca bolgeye ozgu duyurular
  atlaniyor. **Kapsam notu:** duzeltme `Program.EngineWiring`'de, test projesinden
  erisilemiyor - regresyon testi yok.)
- [x] **12B-3 (P2)** - Sektor indeksi 96 sutuna gore cozuluyordu. (YAPILDI:
  `GameWorld.GetSectorByIndex` haritanin kendi izgarasini kullaniyor.)
- [x] **12B-4 (P2)** - MAPLIST etkin haritalari yansitmiyordu. (YAPILDI: `LoadedMaps`,
  `TryGetMapSize`, `TryGetSectorGrid`; resolver bunlardan cevap veriyor. **Kapsam notu:**
  resolver `Program.Scripting`'de; testler onun kullandigi dunya API'sini sabitliyor.)
- [x] **12B-5 (P2)** - Hava degisimi NPC EnvironChange'ine ulasmiyordu. (YAPILDI: yeni
  `GameWorld.CharactersInRegion` bolgenin dokundugu sektorleri yuruyor; paket yine
  yalnizca istemcisi olana gidiyor - referansin ayrimi.)
- [x] **12C-1 (P2)** - Kayit/yukleme dunya saatini sifirliyordu. (YAPILDI: `GAMETIME`
  alani. **Raporun uyarisina uyuldu:** mevcut `TIME` Unix tarihidir, oyun zamani
  sayilmadi; alani tasimayan eski kayit yine yukleniyor.)
- [x] **12C-2 (P2)** - Geciken tick dakikalari kaybediyordu. (YAPILDI: ortak
  `AdvanceWorldClock`, tam dakikalar eklenip kalan korunuyor; iki tick girisi de ayni
  yordami cagiriyor.)
- [x] **12C-3 (P2)** - Tanimsiz Champion START istisna birakiyordu. (YAPILDI: bos liste
  kirpmadan once yanitlaniyor.)
- [x] **12C-4 (P2)** - Kirmizi mum esigi bir adim erken yukseltiyordu. (YAPILDI: esik
  denetimi mum eklenmeden once. **Raporun uyarisi dikkate alindi:** bu bir "off-by-one"
  varsayimi degil, referansin kod sirasi birebir alindi.)
- [x] **12C-5 (P3)** - Mumlarin +4 Z yerlesimi yoktu. (YAPILDI.)
- [x] **12D-1 (P2)** - Altar silininde mumlar kaliyordu. (YAPILDI: `OnAltarDeleted`,
  dunyanin silme callback'ine bagli - .nuke/REMOVE dahil her yol.)
- [x] **12D-2 (P2)** - Canli Champion ayarlari kayitta eskiye donuyordu. (YAPILDI:
  LEVELMAX/SPAWNSMAX durum satirinin SONUNA eklendi - 9 alanli eski satir yine
  parse ediliyor - ve her canli setter aninda kaydediyor.)
- [x] **12D-3 (P3)** - Boss seviyesinde yeni mum olusturulabiliyordu. (YAPILDI: her iki
  renkte son seviye denetimi; kayittan yeniden baglama dali ayri tutuldu.)
- [x] **12D-4 (P2)** - Yeni mumlar itemdef Create hook'unu calistirmiyordu. (YAPILDI:
  `FireCreateTrigger`, yerlestirme ve @AddCandle veto'sundan sonra.)
- [x] **12D-5 (P3)** - Boss sonrasi spawn sayaclari tamamlanmiyordu. (YAPILDI: sayac
  guncellemesi ortak dala tasindi.)

**Tur ici tuzak (kendi hatam, testler yakaladi):** `Serial.IsValid` yalnizca sentinel
`ClearValue` ile karsilastirir, dolayisiyla `default(Serial)` GECERLI raporlar. Yeniden
duzenledigim `AddRedCandle`'in yeniden-baglama dali bunu "kayittan gelen uid" sanip erken
donuyordu ve hicbir kirmizi mum olusmuyordu; iki mevcut Champion testi bunu yakaladi. Dal
artik uid'in gercekten bir esyaya COZULMESINI sart kosuyor.

**12B-12D kapanisi:** tam suite **2.930 basarili / 0 basarisiz** (+21). On uc duzeltme
gecici olarak kapatilarak 16 testin eski davranisi yakaladigi kanitlandi (12B-2 ve
MAPLIST resolver'inin kendisi test projesinden erisilemiyor).

**Acik kalan:** sektor cevre bildiriminin NPC @EnvironChange varyantinin sunucu
entegrasyonu; Champion mum decay'inin seviye dususu ile etkilesimi; dunya saatinin
GameMinuteLengthMs canli degisiminde davranisi.

### 12E-12I - spawn yonetimi ve Champion sozlesmesi: 25 bulgu (6 Eylul 2026)

Kanit raporlari: [12E](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12E_CHAMPION_SCRIPT_KIMLIK.md),
[12F](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12F_CHAMPION_TETIKLEYICI_LEGACY.md),
[12G](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12G_NORMAL_SPAWN_YONETIMI.md),
[12H](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12H_ESYA_SPAWN_KAYIT_ZAMANLAMA.md),
[12I](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12I_SPAWN_YASAM_TETIKLEYICI.md).
Yirmi bes bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

**Kok neden ortak:** Source-X'te yaratik ve esya ureten TEK bir `CCSpawn` bileseni var;
SphereNet'te `SpawnComponent` ve `ItemSpawnComponent` ayri, ve esya tarafinin yarisi hic
baglanmamis. 12G/12H/12I bulgularinin cogu bu ayrimin ayni sonucu.

- [x] **12G-1 (P1)** - DELOBJ uyelik denetimsiz olduruyordu. (YAPILDI: once uyelik, sonra
  yalnizca bag kaldirma; oldurme toplu silme yolunda kaldi.)
- [x] **12G-2 / 12G-3 (P2)** - Canli SPAWNID/ADDOBJ bilesene ulasmiyordu. (YAPILDI.
  **Sinir:** yukleme sirasinda uid henuz olusmamis olabilecegi icin ADDOBJ yalnizca
  nesne gercekten varsa canli baglaniyor; yoksa mevcut relink gecisine kaliyor.)
- [x] **12G-4 / 12G-5 (P2/P3)** - @Spawn konumu ve @Create adi eziliyordu. (YAPILDI:
  konum tetikleyici oncesi/sonrasi karsilastirmasiyla korunuyor; ad yalnizca bos ise
  tanimdan doluyor.)
- [x] **12H-1..12H-5 (P2)** - Esya spawn'i kayit, MOREP, timer, komut ve baslangic
  turunde ikinci sinifti. (YAPILDI: uyeler ADDOBJ olarak yaziliyor ve relink ediliyor;
  MOREP uygulaniyor; item timer bilesene bagli; STOP/START/RESET/DELOBJ eklendi;
  bootstrap bilinen spawn turunu koruyor.)
- [x] **12I-1..12I-5 (P2)** - Tas silme, kap ici uretim, MOREP geri yazimi, grup->tek
  hedef ve @AddObj sure sozlesmesi. (YAPILDI.)
- [x] **12E-1..12E-5 / 12F-1..12F-5** - Champion grup fallback'i, boss silme, tanim
  degisimi, mum uid baglama, CHAMPIONSUMMONED, @Stop kapsami, LEVEL yan etkisi, klasik
  alanlar, olu mum veto'su ve zaman tabani. (YAPILDI.)

**Bilincli kararlar:**
- **LEVEL vs SETLEVEL:** referansin LEVEL anahtari yalnizca alani atar. Yan etkili
  ilerletme kaybolmasin diye ayri bir `SETLEVEL` anahtari eklendi - Source-X'te olmayan
  bir ek, parite denetiminde "fazladan" diye silinmemeli.
- **LASTACTIVATIONTIME tabani degisti:** alan hicbir hesaba girmiyor (yalnizca
  saklaniyor/okunuyor), bu yuzden eski kayittaki UTC saniyesi yalnizca eskimis bir sayi
  olarak kaliyor; raporun uyardigi "eski degerleri oyun milisaniyesi sayma" hatasi
  yapilmadi.
- **`_defSpawnGroups` ayrimi:** tanimin gruplari ile instance override'lari artik ayri
  sozluklerde; NPCGROUP okumasi da etkin grubu (override, yoksa tanim) veriyor.

**Tur ici tuzak (kendi hatam, testler yakaladi):** `Serial.IsValid` yalnizca sentinel
`ClearValue` ile karsilastirdigi icin `default(Serial)` GECERLI raporluyor - "cagiran bir
uid verdi mi" sorusu bununla yanitlanamaz. Yeniden yazdigim mum baglama dallari once bu
tuzaga dustu ve hicbir mum olusmadi; `NamesAUid` (deger != 0) yardimcisi eklendi.

**12E-12I kapanisi:** tam suite **2.963 basarili / 0 basarisiz** (+33). Yirmi bes
duzeltme gecici olarak kapatilarak 28 testin eski davranisi yakaladigi kanitlandi; esya
uyelerinin kayda yazilmasi ayrica tek basina kapatilip dogrulandi. 12H-5 (bootstrap tur
korumasi) `Program.WorldBootstrap` icinde oldugu icin test projesinden erisilemiyor.

**Mevcut test yeniden kuruldu:** `ChampionSystemTests.NpcGroup_...` override temizlemenin
"-1" dondugunu iddia ediyordu; bu tam olarak 12E-1'in kendisi - test artik tanimin
grubuna donusu ve hic tanimlanmamis seviyenin -1 kaldigini dogruluyor.

**Acik kalan:** ADDOBJ'un yukleme sirasinda ileri UID referansi; spawn grubu -> grup
gecisi; Champion mum decay'inin seviye dususuyle etkilesimi; esya spawn'inda PILE ile
kota etkilesimi.

### 12J-12M - spawn gruplari, uyelik ve yapilandirma kaliciligi: 17 bulgu (6 Eylul 2026)

Kanit raporlari: [12J](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12J_SPAWN_TANIM_PRESPAWN_PILE.md),
[12K](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12K_SPAWN_KAPANIS_KOTA_RESET.md),
[12L](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12L_SPAWN_TICK_UYELIK_TEMPLATE.md),
[12M](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12M_SPAWN_CANLI_KAYIT_SINIRLARI.md).
On yedi bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **12J-1..12J-5 (P2)** - Grup agirlik bicimi ve WEIGHT anahtari; @PreSpawn tur
  seciminin host koprusunde kaybolmasi; PILE'in istiflenmeyen esyaya uygulanmasi;
  sayisal kimligin 16 bite kirpilmasi; PILE'in kayitta kaybolmasi. (YAPILDI.)
- [x] **12K-1..12K-3 (P2)** - STOP durumunun kayitta kaybolmasi; canli AMOUNT'un etkin
  kotaya ulasmamasi; RESET'in ev dalinda yutulmasi. (YAPILDI.)
- [x] **12L-1..12L-5 (P2/P3)** - Esya STOP'unun normal tick'i kapsamamasi; ADDOBJ'un
  onceki uyeligi birakmamasi; TEMPLATE hedefinin uretilmemesi; KillAll sirasinda
  yeniden giren DelObj'in temizligi kesmesi; harita kenarinda konum yedeginin
  denenmemesi. (YAPILDI.)
- [x] **12M-1..12M-4 (P1/P2)** - Canli ADDOBJ'un oyuncu ve dolu kota kabul etmesi;
  baslangicta ADDOBJ satirlarinin kapasiteye sayilmasi; NPC MORE2'nin kapasite
  sayilmasi; ayri TIMELO/TIMEHI/MAXDIST alanlarinin uygulanmamasi. (YAPILDI.)

**Onceki turun kendi acigi (rapor yakaladi):** 12L-1'de bildirilen esya STOP tick
kontrolu, gecen turda yazdigim `edit()` cagrisinda bir assertion patlayinca TAMAMI geri
sarildigi icin hic uygulanmamisti; ayni cagrinin ikinci yarisini elle uygulayip birincisi
uygulanmis sanmisim. Simdi uygulandi ve testle sabitlendi.

**12M-1, gecen turun sonucu:** canli ADDOBJ'yi bagladigim yerde denetimsiz
`RegisterExisting` kullanmistim; rapor bunu P1 olarak isaretledi ve hakliydi - artik
denetimli `AddObj` var, `RegisterExisting` yalnizca yukleme yolunda.

**Bilincli kararlar:**
- **Ters grup sirasi korundu:** Source-X sirasi (kaynak,agirlik) birincil; eski
  SphereNet okumasina gore yazilmis paketler bozulmasin diye (agirlik,kaynak) da kabul
  ediliyor - hangi yarinin sayi oldugundan ayirt ediliyor.
- **TEMPLATE acilimi ilk ITEM girdisini aliyor:** tam template semantigi (CONTAINER,
  ic ice, rastgele havuz) bu turda kapsanmadi; acik kalan olarak yazildi.

**12J-12M kapanisi:** tam suite **2.984 basarili / 0 basarisiz** (+21). On yedi duzeltme
gecici olarak kapatilarak 17 testin eski davranisi yakaladigi kanitlandi. 12M-2'nin
`Program.WorldBootstrap` yarisi test projesinden erisilemiyor; testi bunun dayandigi
bilesen sozlesmesini (yeniden baglama kapasiteyi buyutmez) sabitliyor ve bunu acikca
soyluyor.

**Tur ici duzeltilen zayif test:** harita kenari testi once gercek RNG'ye dayaniyordu -
uc denemeden ikisi zaten geciyordu; bilesenin `_rand`'i en dusuk degeri veren biriyle
degistirildi, boylece isabetsizlik kesin.

**Acik kalan:** tam TEMPLATE semantigi (CONTAINER/ic ice/rastgele); grup -> grup gecisi;
yukleme sirasinda ileri UID referansi; spawn kapasitesinin 250 sinirinin Source-X uint16
sozlesmesiyle uzlastirilmasi.

### 12N-12P - spawn'dan ayrilma, kimlik yasam dongusu ve olay sozlesmeleri: 14 bulgu (6 Eylul 2026)

Kanit raporlari: [12N](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12N_SPAWN_PET_OLUM_UID.md),
[12O](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12O_GRUP_TEMPLATE_CANLI_TETIKLEYICI.md),
[12P](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12P_SPAWN_UYELIK_EVENT_ZAMAN.md).
On dort bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

- [x] **12N-1 (P1)** - Evcillestirilen pet spawn uyeliginde kaliyordu. (YAPILDI:
  `Character.ReleaseFromSpawner` hook'u; yalnizca GERCEK sahip degisiminde, kota
  denetimi gectikten sonra.)
- [x] **12N-2 (P2)** - Save'in olu-uye temizligi timer'i acmiyordu. (YAPILDI:
  `CleanupDead` uye kaybinda yeniden zamanliyor.)
- [x] **12N-3 (P1)** - Silinen NPC'nin UID'si yeniden kullanilinca eski spawn yeni
  nesneyi siliyordu. (YAPILDI: silme callback'i uyeligi UID serbest kalmadan kaldiriyor
  - bu, 12E-2'de eklenen `NotifySpawnerOfDeletedMember` yolunun ayni koku; pet devri de
  ayni kapiya baglandi.)
- [x] **12N-4 (P3)** - Uretilen esya OWNED/MOVE_ALWAYS tasiyordu. (YAPILDI.)
- [x] **12O-1..12O-5 (P2)** - Sifir agirlik, sayisal grup uyesi, TEMPLATE kapsayicisi,
  TEMPLATE turunun kayittan geri gelmemesi, canli ADDOBJ'un event tetiklememesi.
  (YAPILDI.)
- [x] **12P-1..12P-5 (P2/P3)** - Home/HOMEDIST devri, DELOBJ sonrasi SPAWNITEM baginin
  kalmasi, @DelObj payload'i ve sure geri uygulamasi, genel @Create sirasi, son slotta
  timer parki. (YAPILDI.)

**Bilincli kararlar:**
- **`SpawnTriggerArgs.SpawnPoint` eklendi:** @DelObj'un O1'i cocuk degil SPAWN NOKTASIDIR;
  mevcut `SpawnedChar`/`SpawnedItem` alanlari da doldurulmaya devam ediyor, boylece
  cocugu okuyan koprular bozulmadi.
- **TEMPLATE kapsayicisi tek seviye:** CONTAINER + onu izleyen ITEM satirlari
  uygulaniyor; ic ice kap ve rastgele havuz hala acik.
- **Genel @Create ertelendi, tanim scripti degil:** referans ikisini ayirir; SphereNet'te
  ikisi tek `OnNpcScriptInit` hook'unda oldugu icin hook'un TAMAMI yerlestirme sonrasina
  alindi. Tanim K/V alanlari zaten daha once uygulaniyor, ama CHARDEF'in kendi
  @Create/@NPCRestock govdesi de artik yerlestirme sonrasinda calisiyor - referansta
  erken calisan bu yari icin bilincli sapma, acik kalan olarak yazildi.

**12N-12P kapanisi:** tam suite **3.002 basarili / 0 basarisiz** (+18). On dort duzeltme
gecici olarak kapatilarak 15 testin eski davranisi yakaladigi kanitlandi.

**Mevcut test yeniden kuruldu:** `SpawnChampionParity12EITests.AddObjIsHandedTheTimer...`
tetikleyicinin her cagrida pozitif saniye gorecegini varsayiyordu; 12P-5 ile son slotta
park -1 gosteriyor. Test artik ilk uyede gercek sureyi, son uyede -1'i ve her iki
durumda scriptin yazdiginin uygulandigini dogruluyor.

**Tur ici duzeltilen kendi hatam:** NPC `DelObj`'ta timer'i tetikleyiciden SONRA
kuruyordum, bu yuzden script her zaman -1 goruyordu; referans once timeout'u kurar sonra
tetikler (:551/:568). Sira duzeltildi.

**Acik kalan:** ic ice TEMPLATE kaplari ve rastgele havuz; CHARDEF'in kendi @Create
govdesinin yerlestirmeden once calismasi; grup -> grup gecisi; yuklemede ileri UID
referansi.

### 12Q-12R - esya timer/decay cekirdegi: 10 bulgu (6 Eylul 2026)

Kanit raporlari: [12Q](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12Q_ESYA_TIMER_DECAY.md),
[12R](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12R_TIMER_KAYIT_TASIMA.md).
On bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

**Kok neden ortak:** Source-X'te esyanin TEK bir saati ve TEK bir `@Timer` kapisi vardir;
decay o saatin bir anlamidir, ayri calisan ikinci bir saat degil. SphereNet'te `DecayTime`
ve `Timeout` ayri ilerliyor, tetikleyici de her iki dalda ayri ayri cagriliyordu. Bu turun
ana isi iki dali tek kapiya indirmekti.

- [x] **12Q-1 (P2)** - @Timer donusu tuketilmiyordu. (YAPILDI: tek kapi; True tick'i
  bitiriyor, False siliyor.)
- [x] **12Q-2 (P2)** - TIMER=-1 decay'i kapatmiyordu. (YAPILDI.)
- [x] **12Q-3 (P2)** - Veto scriptin araligini 30 dakikayla eziyordu. (YAPILDI: veto
  sonrasi yalnizca SIMDI silecek olan decay oteleniyor ve scriptin kendi timer'i varsa o
  kullaniliyor.)
- [x] **12Q-4 (P2)** - Ceset @Timer veto'sunu atliyordu. (YAPILDI: ortak kapi ceset
  dalindan once.)
- [x] **12Q-5 (P2)** - TIMER vermek esyayi silinebilir yapiyordu. (YAPILDI: decay
  aynalanmasi yalnizca ZATEN ATTR_DECAY tasiyan esyada; timer'i dolan ama decay'i olmayan
  esya artik korunuyor.)
- [x] **12R-1 (P2)** - Vadesi gelmis timer kayitta kayboluyordu. (YAPILDI: kurulu timer
  sifir olarak yaziliyor - 12R-2'nin "sifir = simdi" duzeltmesi olmadan tek basina ise
  yaramazdi, ikisi birlikte anlamli.)
- [x] **12R-2 (P2)** - TIMERMS 0/-1'i yok sayiyordu. (YAPILDI.)
- [x] **12R-3 (P2)** - TIMERD hic desteklenmiyordu. (YAPILDI.)
- [x] **12R-4 (P2)** - Yigin bolunmesi timer'i kopyalamiyordu. (YAPILDI.)
- [x] **12R-5 (P2)** - Yerde kurulan timer cantaya tasininca izlenmiyordu. (YAPILDI:
  `RefreshTimerTracking`, ContainedIn degisiminde.)

**Bilincli karar - decay veto'su:** referans veto sonrasi hicbir sure atamaz, ama
SphereNet'te decay ayri bir alan oldugu icin hicbir sey yapmamak esyayi her tick'te
yeniden silinme sinirinda birakirdi. Cozum: yalnizca SIMDI vadesi dolmus decay oteleniyor
ve scriptin kendi TIMER'i varsa o deger kullaniliyor - boylece iki saat ayrisip
scriptin secimini ezmiyor. Iki saatin tumden birlestirilmesi acik kalan olarak durdu.

**12Q-12R kapanisi:** tam suite **3.021 basarili / 0 basarisiz** (+19). On duzeltme
gecici olarak kapatilarak 13 testin eski davranisi yakaladigi kanitlandi.

**Tur ici duzeltilen kendi zayif testim:** "timer'i dolan decay'siz esya silinmiyor"
testi kapatma altinda da geciyordu - eski kodda silme DecayTime uzerinden bir saniye
sonra oluyordu. Teste ayirt edici asama (`DecayTime == 0`) eklendi.

**Acik kalan:** DecayTime ile Timeout'un tek alana indirilmesi; saniye alti decay kaydi;
pickup sirasinda decay iptali; tick callback'inin nesneyi tasimasi/silmesi.

### 12S-12U - decay sozlesmesi, birakma olayi ve gecikmeli is: 15 bulgu (6 Eylul 2026)

Kanit raporlari: [12S](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12S_DECAY_PICKUP_DROP.md),
[12T](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12T_DROP_YASAM_TIMER_TURLERI.md),
[12U](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12U_TIMERF_SEKTOR.md).
On bes bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

**Kok neden ortak:** decay'in *sahibi* belirsizdi. Kim kurar, kim durdurur, kim kenara
cekilir sorusunun cevabi cagri yerine gore degisiyordu; birakma olayi ise referansin
kanallarini (ARGN1 = saniyenin onda biri, string argumani = nokta) hic kullanmiyordu. Tur
once tek bir `SetDecayTime` sozlesmesi kurdu, sonra birakma olayini o sozlesmenin uzerine
oturttu.

- [x] **12S-1 (P2)** - Saniye altindaki decay kayitta yuvarlanip kayboluyordu. (YAPILDI:
  `DECAYMS` milisaniye alani; `DECAY` eski okuyucular icin yaninda yazilmaya devam
  ediyor.)
- [x] **12S-2 (P1)** - Esyayi eline almak curumesini durdurmuyordu; cantadaki esya
  cantada yok olabiliyordu. (YAPILDI: her iki alma noktasinda `SetDecayTime(-1)`.)
- [x] **12S-3 (P1)** - Yere birakma, calisan script timer'inin uzerine decay kuruyordu.
  (YAPILDI: `PlaceItemWithDecay` ortak sozlesmeden geciyor, canli timer'a dokunmuyor.)
- [x] **12S-4 (P1)** - @DropOn_Ground argumanlari referansla ortusmuyordu. (YAPILDI:
  ARGN1 = onda bir saniye, S1 = "x,y,z,map"; ikisi de geri okunuyor.)
- [x] **12S-5 (P2)** - NODECAY bolgesi scriptin secimini veto ediyordu. (YAPILDI: bolge
  DOGAL sureyi script konusmadan once bicimlendiriyor.)
- [x] **12T-1 (P1)** - Scriptin sildigi esya yine de yere konuyordu. (YAPILDI: erken
  cikis + `PlaceItemWithDecay` icinde `IsDeleted` gardi.)
- [x] **12T-2 (P2)** - Scriptin tasidigi kap birakma noktasiyla eziliyordu. (YAPILDI:
  kap degistiyse yerlestirme atlaniyor.)
- [x] **12T-3 (P2)** - RETURN 1 esyayi cantaya geri gonderiyordu. (YAPILDI: bu olayda
  RETURN 1 basari yolu - yerlestir ve onayla.)
- [x] **12T-4 (P2)** - Reddedilen @Timer ozel esya turlerinin isini de durduruyordu.
  (YAPILDI: yalnizca varsayilan yol siliyor.)
- [x] **12T-5 (P2)** - Milisaniyelik timer'i biten ATTR_DECAY esyasi silinmiyordu.
  (YAPILDI: silme karari her iki saati de goruyor.)
- [x] **12U-1 (P1)** - TIMERF CLEAR/STOP sessizce hicbir sey yapmiyordu. (YAPILDI:
  zamanlamadan once ayriliyorlar; STOP isim ya da sonu yildizli aile aliyor.)
- [x] **12U-2 (P2)** - ISTIMERF yoktu. (YAPILDI: kalan milisaniye, yoksa 0.)
- [x] **12U-3 (P2)** - TIMERF suresi duz tamsayi olarak okunuyordu. (YAPILDI: ilk virgul
  VEYA boslukta bitiyor; Sphere sayisi - basi sifir onaltilik, basit toplam; okunamayan
  ya da negatif deger reddediliyor.)
- [x] **12U-4 (P2)** - Vadesi gelen isler zamanlanma sirasiyla calisiyordu. (YAPILDI:
  vadeye gore siralaniyor.)
- [x] **12U-5 (P2)** - Curuyen son kristalden sonra sektor sonsuza dek dinleyici
  bildiriyordu. (YAPILDI: tick `RemoveItem` uzerinden gidiyor.)

**KIRICI degisiklik:** @DropOn_Ground'da ARGN1'in anlami degisti (koordinat -> onda bir
saniye decay). Eski okumaya gore yazilmis paketler icin uyumluluk dali olarak LOCAL.DECAY
saniye cinsinden okunmaya devam ediyor ve script onu *gercekten degistirdiyse* kazaniyor.
`DECAY` alani da `DECAYMS` ile birlikte yazilmaya devam ediyor, boylece kaydi okuyan eski
bir surum bozulmuyor.

**Kendi 12Q duzeltmemdeki kapsam hatasi:** 12T-4 ve 12T-5 bir onceki turda kurdugum tek
`@Timer` kapisinin kapsam hatalariydi - kapi dogruydu, ama silme karari hem ozel turleri
hem de milisaniye saatini disarida birakiyordu. Karar artik tick'in en sonunda ve iki
saatle birlikte veriliyor.

**12S-12U kapanisi:** tam suite **3.047 basarili / 0 basarisiz** (+26). On bes duzeltme
gecici olarak kapatilarak 21 testin eski davranisi yakaladigi kanitlandi.

**Tur ici duzeltilen kendi zayif testim:** "RETURN 1 esyayi yerde birakir" testi once
esyayi yerden aliyordu, bu yuzden geri sekme de ayni yere koyuyor ve test kapatma altinda
da geciyordu. Test artik esyayi cantadan cikariyor; geri sekme cantaya donus olarak
gorunur oluyor.

**12Q'dan devrilen acik kalanlar kapandi:** saniye alti decay kaydi (12S-1) ve
pickup sirasinda decay iptali (12S-2) bu turda cozuldu.

**Acik kalan:** `DecayTime` ile `Timeout`un tumden tek alanda birlestirilmesi (12Q'dan
devrediyor); TIMERF suresinde tam ifade motoru (su an basit toplam/cikarma).

### 12V - TIMERF kaliciligi ve gecikmeli cagri baglami: 5 bulgu (6 Eylul 2026)

Kanit raporu: [12V](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12V_TIMERF_KAYIT_BAGLAM.md).
Bes bulgu da bagimsiz olarak dogrulandi ve tek turda uygulandi.

**Kok neden ortak:** gecikmeli isin *kimin adina, hangi sirayla ve ne zaman* calistigi
hicbir yerde tanimli degildi. Zamanlama tarafinda isler calismadan once toplu
cikariliyor, kalicilikta klasik bolum hic okunmuyor, cagri tarafinda ise SRC, konsol,
arguman hazirligi ve dispatch sirasi referansin dordunu de birden kaciriyordu.

- [x] **12V-1 (P2)** - Klasik [TIMERF] bolumu yuklemede sessizce atlaniyordu. (YAPILDI:
  `LoadTimerFSection`; TimerFCall/TimerFNumbers cifti, 99 = milisaniye / kucugu = 0.56
  onda bir saniye, UID ile nesneye yeniden baglama.)
- [x] **12V-2 (P2)** - Vadesi gelen isler ilk callback'ten once toplu cikariliyordu;
  callback icinden alinan kayit bekleyenleri gormuyordu. (YAPILDI: `PeekDueTimerF` +
  `RemoveTimerFEntry`; is calismadan hemen once cikariliyor.)
- [x] **12V-3 (P2)** - Gecikmeli fonksiyonda SRC esyanin kendisiydi. (YAPILDI:
  `GetTopLevelObj()`; karakterse o, degilse sunucu.)
- [x] **12V-4 (P2)** - Fonksiyon argumani ARGN alanlarina ayristirilmiyordu. (YAPILDI:
  `TriggerArgs.InitFromRaw` - bastaki sayilar ARGN1/2/3.)
- [x] **12V-5 (P2)** - Script fonksiyonu ayni adli yerlesik verb'u golgeliyordu.
  (YAPILDI: once verb tablosu, sonra fonksiyon.)

**Raporun yakalamadigi yan bulgu - yetki yukselmesi:** gecikmeli satir sahibinden
bagimsiz olarak sunucu konsoluyla (Admin) calisiyordu; yani bir oyuncunun cantasindaki
esyanin TIMERF'i motor komutlarini yonetici yetkisiyle isletebiliyordu. Referans, ust
duzey nesne karakterse o karakteri konsol yapar. Artik sahibinin istemcisi, cevrimdisiysa
sahibinin yetki seviyesini tasiyan bir konsol veriliyor.

**Yapisal karar:** SRC/konsol cozumu, dispatch sirasi ve arguman hazirligi host
lambda'sindan `SphereNet.Game.Scripting.DelayedCallDispatcher` sinifina tasindi; boylece
sozlesme sunucu ayaga kaldirilmadan test edilebiliyor. Host yalnizca istemci arama ve
sunucu konsolunu veriyor.

**Bilincli sinir - klasik bolum yalnizca OKUNUYOR:** SphereNet gecikmeli isleri nesne
kaydinin icinde (`TIMERF=<ms>|<ad>|<arg>`) yazmaya devam ediyor. Klasik bolumu ayrica
yazmak, kendi kaydimizi geri yuklerken ayni isin iki kez kurulmasina yol acardi.

**12V kapanisi:** tam suite **3.064 basarili / 0 basarisiz** (+17). Bes duzeltme gecici
olarak kapatilarak 10 testin eski davranisi yakaladigi kanitlandi.

**Acik kalan:** callback icinden yeni is eklenmesi (bu gecise mi yoksa sonrakine mi
dusmeli); yurutme aninda sahip degistirme; klasik TIMERF'in birden cok dosyaya yayilmis
ya da eksik ciftleri.

### 12W - TIMERF desen, ifade ve sorgu sinirlari: 3 bulgu (7 Eylul 2026)

Kanit raporu: [12W](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12W_TIMERF_DESEN_IFADE_SORGU.md).
Uc bulgu de once kod uzerinden yeniden dogrulandi (12X/12Y dalgasi ayni dosyalara
dokunmustu), sonra tek turda uygulandi.

**Kok neden ortak:** referans hem `TIMERF STOP` hem `ISTIMERF` icin desenle KOMUTUN
TAMAMINI eslestirir (`Str_Match(pattern, command) == MATCH_VALID`,
CTimedFunctionHandler.cpp:19/34); SphereNet ikisini de basi eslesen arama olarak
uyguluyor.

- [x] **12W-1 (P2)** - `ClearTimerF` ve `GetTimerFRemaining` sondaki yildizi silip
  yalnizca `FunctionName` uzerinde `StartsWith` uyguluyor (ObjBase.cs:413/430).
  Argumanlar eslestirmeye hic katilmiyor: "f_job" deseni "f_job_extra" isini de
  durduruyor, "f_jo?" hicbir isi durdurmuyor ve "f_job alpha" ile argumanla secilen is
  iptal edilemiyor. Referansta desen KOMUTUN TAMAMIYLA - argumanlar dahil - Str_Match
  ile karsilastirilir; duz isim tam eslesme, `*` ve `?` desen anlamindadir.
  (YAPILDI: `SpherePattern` port edildi; eslesme hedefi komutun tamami.)
- [x] **12W-2 (P2)** - `ScheduleTimerF` sureyi ilk virgul veya BOSLUKTA kesiyor,
  `TryParseSphereDelay` ise yalnizca toplama/cikarma terimlerini isliyor. "2*3, f_done"
  ve "(1+1), f_done" hic is eklemiyor; "1 + 1, f_done" ise `+` adli bir is kuruyor.
  Referans ifadeyi Exp_Get64Val ile TUKETIP komut ayiricisina ilerler (CObjBase.cpp:2777,
  CExpression.cpp:794/1256). 12U'nun "Sphere sayisi olarak oku" duzeltmesinin kapsam
  siniri; ayni arizanin tekrari degil. (YAPILDI: `ScriptNumber.TryEvaluatePrefix`
  ifadeyi tuketip nerede bittigini bildiriyor; komut orada basliyor.)
- [x] **12W-3 (P3)** - `ISTIMERF` eslesmelerin EN KUCUK suresini donduruyor; ustelik
  `best == 0` hem "eslesme yok" hem gecerli bir sonuc oldugu icin vadesi dolmus isin
  sifiri sonraki pozitif sureyle eziliyor. Referans ILK eslesmede doner ve yeni isi kabin
  sonuna ekler (CTimedFunctionHandler.cpp:19/111). Minimum sure bilincli bir tercih
  olacaksa Source-X'ten ayrildigi belgelenmeli. (YAPILDI: ilk eslesme donuyor,
  sifir gecerli sonuc.)

**Yapisal karar - desen dili ortak:** Str_Match'in port'u
`SphereNet.Core.Types.SpherePattern` olarak ayri duruyor ('*', '?', '[a-z]', '[!...]',
'\\' kacisi, buyuk/kucuk harf duyarsiz, bastan sona eslesme). Eslesme hedefi
`FunctionName + " " + Args` olarak yeniden kuruluyor; referans ham komut satirini
sakladigi icin "f_capture=37" gibi ayiricisi esittir olan bir kayitta desen metni
farklidir - bu bilincli yaklasim, sinir olarak not edildi.

**12W kapanisi:** tam suite **3.241 basarili / 0 basarisiz** (+40). Yeni test:
`DelayedCallParity12WTests`. Uc duzeltme gecici olarak geri alinarak 40 testin 12'sinin
eski davranisi yakaladigi kanitlandi.

**Acik kalan:** kose parantezli desenlerin gercek script paketinde kullanimi
denenmedi; ifade okuyucusu degisken/property cozmuyor (yorumlayici satiri zaten
cozmus olarak veriyor).

### 12X-12Y - gecikmeli isin vade sirasi, komut ayrimi ve referans zinciri: 9 bulgu (6 Eylul 2026)

Kanit raporlari: [12X](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12X_TIMERF_COKLU_NESNE_PAYLOAD.md),
[12Y](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12Y_TIMERF_REFERANS_KAYIT_SINIRLARI.md).
Dokuz bulgu da dogrulandi ve tek turda uygulandi (`5e16269`).

**Kok neden ortak:** vade sirasi NESNE BASINA tutuluyordu ve gecikmeli satirin
ayristirilmasi motorun kendi komut dilbilgisini kullanmiyordu. Referans butun zamanli
nesneleri tek bir vadeye gore sirali listede tutar (CWorldTicker.cpp:1051/1129/1214) ve
payload'i tetiklenme aninda `ParseKey` ile ayirir (CTimedFunction.cpp:88).

- [x] **12X-1 (P2)** - Nesneler arasi vade sirasi korunmuyordu; 10 saniye once vadesi
  gelen is, sahibi kumede sonra geldigi icin 100 ms'lik isten sonra calisiyordu.
  (YAPILDI: hazir isler butun nesnelerden toplanip vadeye gore siralaniyor, esitlikte
  toplama sirasi; is calismadan hemen once cikariliyor, boylece callback icindeki Save
  bekleyenleri gormeye devam ediyor.)
- [x] **12X-2 (P2)** - Ne verb ne fonksiyon olan gecikmeli satir hicbir sey yapmiyordu.
  (YAPILDI: referansin son adimi - `r_LoadVal` property atamasi - eklendi;
  TOPOBJ./CONT./LINK. zincirinde de eksikti.)
- [x] **12X-3 (P2)** - Payload yalnizca bosluktan ayriliyor, "=" ad icinde kaliyordu;
  "TIMERF 0, f_capture=37" cozulemeyen bir is kuruyordu. (YAPILDI: ortak ayirici
  yardimcisi - "=, \t" ayiricilari, tirnak/parantez kurallari, yalnizca ilk ayirici
  yapisal.)
- [x] **12X-4 (P2)** - Harf iceren Sphere hex argumani sifir okunuyor ve parse imlecini
  de bozuyordu ("0A,2,3" -> 0,0,0). (YAPILDI: bastaki '0' hex ISARETI, ardindan
  [0-9A-Fa-f]; anlamli nibble genisligi isaretli yeniden yorumu belirliyor.)
- [x] **12X-5 (P3)** - Script sayisal argumanlari 32 bit sinirinda doyuyordu.
  (YAPILDI: tasima ucundan uca 64 bit, daralma motorun kendi alaninda ve saturasyonla.)
- [x] **12Y-1 (P2)** - Istemcisiz oyuncu/NPC icin UNEQUIP/SUMMONTO/CONTROL karakteri
  cozemiyordu. (YAPILDI: `ITextConsole.GetSourceChar`; istemcisiz karakterin vekil
  konsolu da yanitliyor.)
- [x] **12Y-2 (P2)** - Adi taniyip reddeden yerlesik verb, ayni adli script
  [FUNCTION]'ina dusuyordu. (YAPILDI: `TryExecuteCommand` adin verb tablosuna ait olup
  olmadigini ayrica bildiriyor; sahiplenilen adin yaniti kesin.)
- [x] **12Y-3 (P2)** - TOPOBJ./CONT./LINK. zinciri hedefi cozup yalnizca verb ve
  property deniyordu; "TOPOBJ.f_mark 37" sessizce yutuluyordu. (YAPILDI: cozulen nesnede
  tam sira - verb, fonksiyon, property.)
- [x] **12Y-4 (P2)** - Nesne basina 64 is siniri 65.'yi sessizce dusuruyor, klasik
  yukleyici de dusen isi geri yuklenmis sayiyordu. (YAPILDI: `AddTimerF` kabul edilip
  edilmedigini bildiriyor; kayittan yukleme siniri asiyor - kayit otoritedir - canli
  planlamada sinir duruyor ve ret loglaniyor.)

**Ayrica:** 12X'in "=" ayrimi klasik yukleme yolunda da suruyordu; ayni yardimci oraya
da baglandi (CTimedFunctionHandler.cpp:185).

### 12Z - callback icinden eklenen isin ayni tura girmesi: 1 bulgu (7 Eylul 2026)

Kanit raporu: [12Z](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_12Z_TIMERF_TICK_SECIM_YASAM.md).

- [x] **12Z-1 (P2)** - Callback icinde baska nesneye eklenen sifir gecikmeli is, hedef
  aktif ve henuz dolasilmamissa AYNI turda calisiyordu. (YAPILDI: 12X-1'in vade sirasi
  duzeltmesi tur uyeligini hicbir callback calismadan once sabitledigi icin bu bulgu
  kendiliginden kapandi; dort hedef durumunun tamami testle kilitlendi.)

### 13A-13E - cagri zinciri, nesne fabrikalari ve kopyalama: 23 bulgu (7 Eylul 2026)

Kanit raporlari: [13A](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_13A_SCRIPT_CALL_TRY_ARGN.md),
[13B](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_13B_TRYSRC_NEWITEM_NEWNPC.md),
[13C](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_13C_NEW_ACT_REFERANS_YASAMI.md),
[13D](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_13D_KOPYALAMA_ICERIK_BILESEN.md),
[13E](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_13E_DUPE_YERLESIM_SAYISAL.md).
Yirmi uc bulgu dogrulandi ve tek turda uygulandi (`cc65b64`).

**Kok neden ortak:** cagri ve fabrika yuzeylerinin hicbirinde referansin ARGUMAN, REFERANS
ve BASARISIZLIK sozlesmesi yoktu. CALL kendi arg nesnesini uyduruyordu, fabrikalar NEW'i
yan etki olarak yaziyordu, kopyalama ise nesnenin yalnizca kendi alanlarini tasiyordu.

- [x] **13A-1 (P2)** - CALL hicbir sey hazirlamiyordu: cagiranin ARGN1/2/3'unu kopyalayip
  yalnizca ARGS'i degistiriyor, argumansiz CALL ise ARGS'i bosaltiyordu. (YAPILDI:
  referansin Execute_Call'i gibi CAGIRANIN KENDI arg nesnesi kullaniliyor - argumanla
  gelen sayilar/nesne/metin saklanip yeni argumandan Init ediliyor ve donuste geri
  aliniyor; argumansiz bicimde nesne oldugu gibi geciyor. Duz fonksiyon satiri ARGN'yi
  KENDI argumanindan hazirliyor.)
- [x] **13A-2 (P2)** - CALL cagiranin LOCAL havuzunu paylasmiyordu. (YAPILDI: havuz arg
  nesnesinde; ayni nesnenin gecmesi paylasimi cift yonlu yapiyor, duz fonksiyon satiri
  ise kendi havuzunu koruyor.)
- [x] **13A-3 (P2)** - "CALL SRC.f_mark" / "CALL TOPOBJ.f_mark" literal ad araniyordu.
  (YAPILDI: referans basi `r_GetRef` ile cozuluyor, SRC icin `pSrc->GetChar`; kalan ad
  cozulen nesnede cagriliyor.)
- [x] **13A-4 (P2)** - TRY kendi kisitli yurutucusune sahipti; "TRY TAG.FLAG 1" ve
  "TRY f_mark" hicbir sey yapmiyordu. (YAPILDI: satirin kalani hedefin tam `r_Verb`
  zincirinden geciyor, yalnizca gecersiz referans hatasi bastiriliyor.)
- [x] **13A-5 (P2)** - ARGN atamasi ondalik/0x parser kullaniyordu; "ARGN1=010" on,
  "ARGN1=1+1" reddediliyordu. (YAPILDI: atama tam ifade yolundan.)
- [x] **13B-1 (P2)** - TRYSRC kaynagi degil hedefi degistiriyordu. (YAPILDI: uid kaynak
  konsoluna cozuluyor, verb MEVCUT nesnede calisiyor; koprulenmis konsol o karakter adina
  konusuyor.)
- [x] **13B-2 (P2)** - SERV.NEWITEM TEMPLATE basligini kaynak turu kontrolunde
  reddediyordu. (YAPILDI: CreateHeader ITEMDEF ve TEMPLATE'i birlikte kabul ediyor;
  template acilimi spawner ile ortak tek uygulamada.)
- [x] **13B-3 (P2)** - Karakter parent'inda esya her zaman cantaya gidiyordu. (YAPILDI:
  tanimdaki katmana giydiriliyor, katman yoksa cantaya - LoadSetContainer.)
- [x] **13B-4 (P2)** - NEWITEM argumani uc alana bolunuyor, dorduncu alan parent uid'ine
  yapisiyordu. (YAPILDI: dort alan.)
- [x] **13B-5 (P2)** - SERV.NEWNPC yeni yaratigi 0 can / 0 mana ile birakiyordu.
  (YAPILDI: olusturmada uc havuz da doluyor - CreateNewCharCheck.)
- [x] **13C-1 (P2)** - NEW ve NEW.<property> farkli alanlardan okuyordu; ustelik boslik
  testi "!= 0" iken bos deger 0xFFFFFFFF idi. (YAPILDI: iki bicim tek referanstan;
  SERV.LASTNEWITEM/LASTNEWCHAR kendi alanlarini koruyor.)
- [x] **13C-2 (P2)** - Fabrika NEW'i nesne olusturmanin yan etkisi olarak yaziyordu;
  ic ice @Create ikinci esya yaptiginda referans ona kayiyordu. (YAPILDI: NEWITEM
  referansi isinin SONUNDA yaziyor.)
- [x] **13C-3 (P2)** - Nesne uzerine yazilan duz NEWITEM, o nesnenin ACT'ini
  guncellemiyordu. (YAPILDI: komut sunucuya yonelmemisse cagiranin ACT'i olusturulani
  gosteriyor.)
- [x] **13C-4 (P2)** - NEW yalnizca okunabiliyordu; "NEW=<uid>" ve "NEW.<prop>=<deger>"
  sessizce yok sayiliyordu. (YAPILDI: ikisi de bagli; hicbir seyi adlandirmayan atama
  referansi temizliyor.)
- [x] **13C-5 (P2)** - Kaynagi bulunmayan NEWDUPE onceki NEW'i birakiyordu. (YAPILDI:
  referans temizleniyor ve basarisizlik donuyor.)
- [x] **13D-1 (P2)** - Kapsayici kopyasi bos cikiyordu; kopyalama yigin-bolme
  yardimcisini paylasiyordu. (YAPILDI: her cocuk kendi kopyasi olarak, alt kapsayicilar
  dahil.)
- [x] **13D-2 (P2)** - Kopyalanan spawner TYPE'ini tasiyip davranisini kaybediyordu.
  (YAPILDI: kota, aralik, menzil ve spawn kimligi tasiniyor; uretilmis cocuklar bilincli
  olarak tasinmiyor.)
- [x] **13D-3 (P2)** - Kopyalanan karakter ciplak ve cantasiz cikiyordu. (YAPILDI: her
  giyili katman yeni karaktere kopyalaniyor.)
- [x] **13D-4 (P2)** - Kopyalanan karakter fame/karma, direncler, durum bayraklari
  (gizlenme dahil) ve EVENTS listesini kaybediyordu. (YAPILDI: DupeFrom sozlesmesi;
  hesap/istemci/uid gibi canli baglar bilincli olarak kopyalanmiyor.)
- [x] **13D-5 (P3)** - Kitap AUTHOR yazilabiliyor ama okunamiyordu (setter TAG.AUTHOR,
  getter TAG.BOOK_AUTHOR). (YAPILDI: setter iki yazimi da yaziyor, getter ikisini de
  kabul ediyor - ne pano yolu ne de mevcut kayit veri kaybediyor. Bu kaynak kitapta da
  vardi, kopyalama kaybi sayilmadi.)
- [x] **13E-1 (P2)** - DUPE/NEWDUPE kopyalari kaynagin kapsayicisina itiliyordu.
  (YAPILDI: MoveNearObj gibi UST DUZEY nesnenin dunya konumu; kapsayici-ici koordinat
  haritada yanlis noktaya donusuyordu.)
- [x] **13E-2 (P2)** - NEWDUPE uid'i kosulsuz hex okuyordu. (YAPILDI: Sphere arguman
  kurali - yalnizca bastaki sifir hex.)
- [x] **13E-3 (P2)** - DUPE adedi ondalik int okunuyordu; "1+1" bire dusuyor, "010" on
  kopya yapiyordu. (YAPILDI: ortak Sphere sayi parser'i.)

### 13F - TEMPLATE recetesinin yurutulmesi: 5 bulgu (7 Eylul 2026)

Kanit raporu: [13F](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_13F_TEMPLATE_RECETE_YURUTME.md).
Bes bulgu da dogrulandi ve tek turda uygulandi (`7638841`).

**Kok neden ortak:** tarif bir RECETE, tek bir itemdef degil. SphereNet ilk satiri okuyup
nesneyi elle kuruyordu; referans satir satir yurutur (ReadTemplate, CItem.cpp:600) ve her
olusturma satirini CreateHeader'dan gecirir (:461).

- [x] **13F-1 (P2)** - Recetenin miktarlari uygulanmiyor, sifir miktar yine esya
  uretiyordu. (YAPILDI: R# sansi, "{lo hi}" zari dahil miktar ifadesi ve sifirda satirin
  reddi; kok satirin miktari cagirana donen nesneye de ulasiyor.)
- [x] **13F-2 (P2)** - NAME/COLOR/TAG satirlari yuklemede dusuruluyordu. (YAPILDI:
  TemplateDef her satiri KAYNAK SIRASIYLA tutuyor; property satiri kendinden once
  olusturulana uygulaniyor.)
- [x] **13F-3 (P2)** - Ikinci CONTAINER satiri sonrakilerin hedefi olmuyordu. (YAPILDI:
  ITC_CONTAINER; gercekte kapsayici olmayan satir hicbir hedef birakmiyor.)
- [x] **13F-4 (P2)** - Baska bir TEMPLATE'i adlandiran ITEM satiri hicbir sey
  uretmiyordu. (YAPILDI: cozucu kaynak TURUNU koruyor ve derinlik siniriyla
  ozyineliyor.)
- [x] **13F-5 (P2)** - Kaynak konumundaki ondalik sayi hex okunuyordu. (YAPILDI: Sphere
  sayi kurali.)

**Yapisal karar:** TEMPLATE basligiyla cagrilan SERV.NEWITEM artik receteyi YURUTUYOR ve
kendi sonucunu donduruyor; spawner ayni yuruyusu paylasiyor.

### 13G-13H - script fabrikasi yerlestirmesi ve tarif sinirlari: 10 bulgu (7 Eylul 2026)

Kanit raporlari: [13G](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_13G_NEWITEM_YERLESTIRME_HATA_SONUCU.md),
[13H](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_13H_TEMPLATE_BAGLAM_OZEL_KAPLAR.md).
On bulgu da dogrulandi ve tek turda uygulandi (`855ee3a`).

**Kok neden ortak:** iki tarafta da EKSIK OLAN, basarisizligin ne anlama geldigiydi.
Fabrika tarafinda reddedilen yerlestirme basari gibi raporlaniyordu; tarif tarafinda
hicbir sey uretmeyen satir onceki nesneyi hedef olarak birakiyordu.

- [x] **13G-1 (P2)** - NEWITEM miktar alani en az bire yuvarlaniyor ve ilk operatorde
  duruyordu ("1+1" -> 1, "0" -> 1). (YAPILDI: `ScriptNumber.ToScriptAmount`; ifade
  degerlendiriliyor ve sifir sifir kaliyor - SetAmount sozlesmesi.)
- [x] **13G-2 (P2)** - Dolu kapsayicinin reddi basari gibi donuyordu; esya 0,0,0'da
  parent'siz kalirken script gecerli uid aliyordu. (YAPILDI: yerlestirme sonucu cagirana
  ulasiyor - nesne kaldiriliyor ve NEWITEM 0 donuyor. 255 esya siniri korundu: istemci
  sinirini dusunmeden kapasiteyi kaldirmak onerilmemisti.)
- [x] **13G-3 (P2)** - LAYER=Pack nesnesi giydirme dalindan cikariliyor, cantasiz
  karakterde IKINCI bos canta uretilip istenen canta onun icine saklaniyordu. (YAPILDI:
  BOS pack katmani kapsayiciyi kabul ediyor; dolu katman reddediyor ve nesne zaten giyili
  cantaya dusuyor - CanEquipLayer.)
- [x] **13G-4 (P2)** - Dorduncu alan (equip flag) ayristirilip hic okunmuyordu.
  (YAPILDI: flag=1 karakter parent icin ItemEquip yolu - guc sarti, @EquipTest vetosu,
  giyildikten sonra @Equip; flag=0 yukleme tarzi yol.)
- [x] **13G-5 (P2)** - Basarisiz NEWITEM/NEWNPC NEW'i temizlemiyordu. (YAPILDI: iki yol
  da temizliyor - NEWDUPE'un 13C-5'te aldigi ayni sozlesme.)
- [x] **13H-1 (P2)** - Basarisiz olusturma satirindan sonraki property onceki esyaya
  uygulaniyordu. (YAPILDI: ITC_CONTAINER referansi test etmeden once atar; basarisiz
  satir hicbir hedef birakmiyor, hedef kapsayici ise oldugu gibi kaliyor.)
- [x] **13H-2 (P2)** - Alt TEMPLATE satirinin dis miktari uygulanmiyordu. (YAPILDI:
  CreateHeader miktari urettigi seye - template sonucu dahil - uyguluyor ve yalnizca
  miktar 1 degilse uzerine yaziyor.)
- [x] **13H-3 (P2)** - Yalnizca iki esya turu tarif hedefi sayiliyor, ceset ve banka
  kutusu bos kaliyordu. (YAPILDI: `Item.IsContainerType` - turun karsilik geldigi C++
  sinifi, CreateBase; CItemCorpse de CItemContainer'dan turer.)
- [x] **13H-4 (P2)** - Tarif icindeki FUNC satiri property gibi atanip
  cagrilmiyordu. (YAPILDI: `TemplateRowKind.Func` + `TemplateEngine.FunctionRowHook`;
  cagiran, kapsayicinin ust duzey nesnesi, kapsayici yoksa sunucu. Cagrildigi nesneyi
  silen fonksiyon tarifin esya referansini da sonlandiriyor.)
- [x] **13H-5 (P2)** - Create sirasinda tasinamaz yapilan esya kapsayiciya ekleniyordu.
  (YAPILDI: `Item.IsMovableType`; satir hicbir sey uretmemis sayiliyor ve nesne
  siliniyor. Kapsayicisiz tasinamaz nesne etkilenmiyor.)

**Raporun iddiasindan bilincli sapma - 13G-4 guc kontrolu:** rapor guc sartinin flag=0
yolunda da uygulandigini soyluyordu. `CanEquipLayer` icinde `CanEquipStr`, YALNIZCA
katmani kendisi turetmek zorunda kaldiginda calisir (CCharStatus.cpp:326); LoadSetContainer
tanimdaki katmani hazir verdigi icin flag=0 yolunda guc testi YOKTUR. Kod referansa gore
yazildi, test de bu ayrimi kilitliyor.

**Yapisal karar:** yerlestirme kurallari `SphereNet.Game.Objects.Items.ScriptItemPlacement`
sinifina tasindi; boylece sozlesme sunucu ayaga kaldirilmadan test edilebiliyor. Host
yalnizca @EquipTest/@Equip tetikleyicilerini bagliyor.

**13G-13H kapanisi:** tam suite **3.184 basarili / 0 basarisiz**. Yeni testler:
`ScriptFactoryParity13GTests`, `TemplateRecipeParity13HTests` (+41 test, 13F dahil).

**Acik kalan:** 13G raporunun 4. maddesinin tetikleyici kuyrugu (flag=1'de @EquipTest
veto sonrasi NPC/oyuncu ayrimi); tarif icindeki BUY/SELL satirlarinin vendor katmanlari;
FUNC satirinin ARGN hazirligi tam script yuruyusuyle canli dogrulanmadi.

### 13I-13J - spawner recete yolu ve kopyanin tasidiklari: 8 bulgu (7 Eylul 2026)

Kanit raporlari: [13I](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_13I_SPAWN_TEMPLATE_ORTAK_YOL.md),
[13J](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_13J_KOPYA_BONUS_REFERANS_KONUM.md).
Sekiz bulgu da dogrulandi ve tek turda uygulandi.

**Kok neden ortak:** her iki tarafta da AYNI isi yapan IKI yol vardi. Recete kurmanin
biri (BuildTemplate) 13F'de referansa gore duzeltilmisti, spawner ise hala kendi
"ilk itemdef'i bul ve elle kur" yolunu kullaniyordu. Kopyalamanin biri (CreateDupe)
13D'de duzeltilmisti, karakterin kendi DUPE verb'u ise hala elle alan kopyaliyordu.

- [x] **13I-1 (P2)** - Spawner kok satirin miktarini, sifirla kapatmayi ve satir
  ayarlarini yurutmuyordu. (YAPILDI: TEMPLATE hedefi artik BuildTemplate ile yurutuluyor
  ve donen nesne spawn ediliyor - GenerateItem, CCSpawn.cpp:323.)
- [x] **13I-2 (P2)** - Ilk satiri baska TEMPLATE olan tarif spawner'da hic nesne
  uretmiyordu. (YAPILDI: ayni ortak yuruyus basvurulan receteyi izliyor.)
- [x] **13I-3 (P2)** - Tarif icerigi ve ayarlari @Spawn'dan SONRA uygulaniyordu.
  (YAPILDI: nesne tetikleyiciden once tamamlaniyor; tetikleyicinin yazdigi ad artik
  ezilmiyor.)
- [x] **13I-4 (P2)** - @PreSpawn hedefi degistirince kok yeni, icerik eski receteden
  geliyordu. (YAPILDI: tur ve icerik, tetikleyicinin sectigi indeksten cozuluyor.)
- [x] **13J-1 (P2)** - Karakter kopyasi ETKIN havuzlari temel alana yaziyordu; ekipman
  bonusu her kopyada katlaniyor, kiyafet cikinca geri gelmiyordu. (YAPILDI: BaseMax*
  kopyalaniyor; mevcut havuzlar ekipmandan sonra uygulaniyor.)
- [x] **13J-2 (P2)** - Giyili esyanin karaktere oz referanslari eski karakterde
  kaliyordu. (YAPILDI: MORE1/MORE2/LINK yalnizca kaynagi gosteriyorsa kopyaya
  ceviriliyor; baska nesneye giden bag korunuyor. Referansin mount dali modellenmedi.)
- [x] **13J-3 (P2)** - Karakterin kendi DUPE verb'u merkezi kopyalamayi kullanmiyordu.
  (YAPILDI: DUPE artik CreateDupe cagiriyor; arguman newbie sozlesmesini seciyor -
  birden kucuk deger ekipmani ve icerigini ATTR_NEWBIE yapar.)
- [x] **13J-4 (P3)** - Canta kopyasinda icerik duzeni korunmuyordu. (YAPILDI: her cocuk
  kopyasi kaynak cocugun kapsayici-ici konumuna yerlestiriliyor.)

**Yapisal karar - tek recete yuruyusu:** `TemplateEngine.FillTemplateContents` ve
spawner'in `ExpandTemplate` sargisi cagiranlariyla birlikte kaldirildi; `WalkRows` da
yalnizca bu yola hizmet eden `skipLeadingContainer`/`topCont` parametrelerinden
arindirildi. 13I'nin dort bulgusunun tamami "iki ayri recete yolu" olmasindan
kaynaklandigi icin ikinci yolun kaldirilmasi duzeltmenin kendisidir.

**13I-13J kapanisi:** tam suite **3.201 basarili / 0 basarisiz** (+17). Yeni testler:
`SpawnTemplateParity13ITests` (8), `DuplicationParity13JTests` (9). 13J duzeltmeleri
gecici olarak geri alinarak dokuz testin besinin eski davranisi yakaladigi kanitlandi;
13I icin ayni geri alma denemesi yapilmadi, dayanak raporun probe kaydidir.

**Acik kalan:** kok satirin R# olasiligi (13I raporu rastgele deney yapmadi); template
-> itemdef ve itemdef -> template gecisleri @PreSpawn'da denenmedi; kopyalamanin mount
dali (referansta more2 uzerinden ikinci karakter uretimi) ve canta ici derin
oz-referanslar.

### IS-5 - canli ini ile motorun karsilastirilmasi (7 Eylul 2026)

Kaynak: port plani IS-5 / PLAN-301. Yontem: canli `C:\sphereNetServer\sphere.ini`
icindeki her anahtar, `SphereConfig` icinde string olarak GECIYOR MU diye tarandi
(yardimci metotlarla okunanlari da yakalamak icin literal taramasi).

**Sonuc: 184 anahtar set ediliyor, 33'u config tarafindan hic okunmuyor.** Yani ini'de
yazan deger sessizce hicbir sey yapmiyor.

Okunmayan 33 anahtarin siniflandirmasi:

| Grup | Anahtarlar | Not |
|---|---|---|
| Bu dalgada baglandi | TELEPORTEFFECTNPC/PLAYERS/STAFF, TELEPORTSOUNDNPC/PLAYERS/STAFF | referans davranisi tanimli, gorunur etki |
| Oynanis - referansta var, sirada | BACKPACKOVERLOAD, NPCTRAINPERCENT, SPELLTIMEOUT, HITSUPDATERATE, CANUNDRESSPETS, NPCCANFIZZLEONHIT, NPCSKILLSAVE, MAXPOLYSTATS, DISTANCEFORMULA, NOWEATHER, MAXHOUSESGUILD, STATSFLAGS, FLIPDROPPEDITEMS | her biri ayri kucuk is |
| Referansta YOK (0.56/yerel) | MONSTERTIGHT | eklenmemeli, sapma olarak yazilmali |
| Ag/altyapi | MAXQUEUESIZE, MAXSIZECLIENTIN/OUT, MAXSIZEPERTICK, USEASYNCNETWORK, USEEXTRABUFFER, USEPACKETPRIORITY, NETWORKTHREADPRIORITY, CONNECTINGMAXIP | bu motorun ag modeli farkli; once tasarim karari |
| Harita/veri | MAP0, MAP1, MAPCACHETIME | harita yukleme yolu ayri |
| Yerel | APPUPDATEREPODIR | SphereNet'e ozgu, motor ayari degil |

- [x] **B5-1 (P2)** - Teleport her zaman ayni sesi caliyor, hicbir efekt gostermiyordu;
  alti ini ayari okunmuyordu. (YAPILDI: aktor sinifina gore efekt+ses, referansin
  varsayilanlariyla; 0 = gosterme/calma.)

**Ayrica ayni listeden onceki dalgada:** MAXITEMCOMPLEXITY (DUPE tavani, uydurma 1000
yerine).

**Kapanis:** tam suite **3.279 basarili / 0 basarisiz** (+1).

**Acik kalan:** yukaridaki "oynanis" grubu (13 anahtar) ve ag/altyapi grubunun tasarim
karari. Ayrica bu tur yalnizca CANLI ini'nin set ettigi anahtarlara bakti; referansin
279 anahtarinin tamami ile karsilastirma ayri bir istir.

**IS-5 ikinci dilim (7 Eylul 2026):** okunmayan anahtarlarin ikinci sinifi bulundu -
**ad uyusmazligi**: ayar var ve calisiyor, ama referansin ANAHTAR ADI kabul edilmiyor.

- [x] **B5-2 (P2)** - NPC egitim ayarlari yalnizca `TrainSkillPercent`/`TrainSkillMax`
  adlariyla okunuyordu; referans (ve shard ini'si) `NPCTRAINPERCENT`/`NPCTRAINMAX` yazar.
  (YAPILDI: iki ad da kabul ediliyor, referansinki oncelikli.)
- [x] **B5-3 (P2)** - `MAXHOUSESGUILD` yoktu; lonca kendi tasi soylemedikce sinirsizdi.
  (YAPILDI: config + `GuildManager.DefaultMaxHouses`, referans varsayilani 1.)
- **HITSUPDATERATE**: referansta ELEM_VOID - kabul eder, hicbir sey yapmaz. Davranis
  UYDURULMADI; sapma olarak kaydedildi.

**Kapanis:** tam suite **3.281 basarili / 0 basarisiz** (+2).

**IS-5 ucuncu dilim (7 Eylul 2026):**

- [x] **B5-4 (P2)** - "Vurulunca yalnizca oyuncu bozulur" kesme yoluna kural olarak
  yazilmisti; referansta bu NPCCANFIZZLEONHIT'e bagli varsayilandir. (YAPILDI: ayar
  eklendi, varsayilan degismedi.)
- **NPCSKILLSAVE - KARAR BEKLIYOR:** referansta kayit ayari degil, esigin altindaki NPC
  skillerini SIFIRLAYAN temizlik kurali (FixWeirdness, CChar.cpp:1030, varsayilan 10).
  Baglamak import edilmis veriyi degistirir.
- **MAXPOLYSTATS - TUKETICI YOK:** polymorph formun statlarini hic uygulamiyor; once o
  eksik kapanmali.

**Kapanis:** tam suite **3.282 basarili / 0 basarisiz** (+1).

**IS-5 dorduncu dilim (7 Eylul 2026):**

- [x] **B5-5 (P2)** - Oyuncunun cantasi duz kapsayici agirlik sinirina tabiydi; guc
  farketmiyordu. (YAPILDI: `BACKPACKOVERLOAD` - canta siniri = sahibinin tasiyabildigi +
  ayar, varsayilan 40 tas; sifir alti = sinirsiz.)
- [x] **B5-6 (P3)** - Yere birakilan esya kosulsuz ceviriliyordu. (YAPILDI:
  `FLIPDROPPEDITEMS` ayari + tasinabilirlik kosulu.)

**Kapanis:** tam suite **3.283 basarili / 0 basarisiz** (+1).

### IS-5 kapanisi - okunmayan 33 ini anahtarinin tam siniflandirmasi (8 Eylul 2026)

Yontem: canli `C:\sphereNetServer\sphere.ini`'nin set ettigi 184 anahtar `SphereConfig`
icinde arandi; okunmayan 33'unun her biri referans tablosundan ve tuketicisinden
dogrulandi. Amac PLAN-002'nin istedigi siniflandirma: okunuyor / davranista tuketiliyor /
bilincli desteklenmiyor.

| Anahtar | Durum | Not |
|---|---|---|
| TELEPORTEFFECTNPC/PLAYERS/STAFF | BAGLANDI | aktor sinifina gore efekt, referans varsayilanlari |
| TELEPORTSOUNDNPC/PLAYERS/STAFF | BAGLANDI | ayni |
| MAXHOUSESGUILD | BAGLANDI | lonca varsayilan ev siniri (referans 1) |
| NPCCANFIZZLEONHIT | BAGLANDI | varsayilan degismedi (yalniz oyuncu bozulur) |
| BACKPACKOVERLOAD | BAGLANDI | canta siniri = sahibin tasiyabildigi + ayar |
| FLIPDROPPEDITEMS | BAGLANDI | + tasinabilirlik kosulu |
| NPCTRAINPERCENT / NPCTRAINMAX | BAGLANDI (alias) | ayar zaten calisiyordu, referansin adi okunmuyordu |
| HITSUPDATERATE | REFERANSTA NO-OP | ELEM_VOID: kabul eder, hicbir sey yapmaz. Davranis uydurulmadi |
| MONSTERTIGHT | REFERANSTA YOK | 0.56/yerel anahtar; eklenmemeli |
| MAXPOLYSTATS | TUKETICI YOK | polymorph formun statlarini hic uygulamiyor; once o eksik |
| SPELLTIMEOUT | TUKETICI YOK | hedef imlecinin sure asimi mekanizmasi yok (varsayilan 0 = sinirsiz, mevcut davranis) |
| STATSFLAGS | TUKETICI YOK | `Stat_SetMax`'in "0 = turet" semantigi burada yok |
| DISTANCEFORMULA | KAPSAM DISI | mesafe metrigini global degistirir; dort varyantin tamami ayri is |
| NOWEATHER | KARAR BEKLIYOR | referans varsayilani "hava KAPALI", bizde acik |
| NPCSKILLSAVE | KARAR BEKLIYOR | kayit ayari degil: esik alti NPC skillerini SIFIRLAYAN temizlik kurali |
| MAP0, MAP1, MAPCACHETIME | KAPSAM DISI | harita yukleme yolu ayri tasarim |
| MAXQUEUESIZE, MAXSIZECLIENTIN/OUT, MAXSIZEPERTICK, USEASYNCNETWORK, USEEXTRABUFFER, USEPACKETPRIORITY, NETWORKTHREADPRIORITY, CONNECTINGMAXIP | KAPSAM DISI | bu motorun ag modeli farkli; once tasarim karari |
| APPUPDATEREPODIR | YEREL | SphereNet'e ozgu, motor ayari degil |

**Sayilar:** 33 anahtardan **13'u baglandi**, 3'u tuketicisi olmadigi icin acik, 2'si
karar bekliyor, 1'i referansta no-op, 1'i referansta yok, 13'u kapsam disi/yerel.

**Ilke:** tuketicisi olmayan anahtara ayar eklenmedi. Bir ini satirinin okunup hicbir sey
yapmamasi, hic okunmamasindan daha kotudur - ilki calistigini soyler.

**IS-5 - karar bekleyen iki madde kapandi (8 Eylul 2026, kullanici karari: "Source-X
nasilsa oyle"):**

- [x] **B5-7 (P2)** - NOWEATHER yoktu; motor havayi kosulsuz calistiriyordu. (YAPILDI:
  ayar eklendi, varsayilan REFERANSINKI = hava yok. Bayrak yalnizca yeni hava atmayi ve
  istemciye soylemeyi durduruyor; scriptin kurdugu hava saklaniyor ve okunabiliyor -
  ilk denemede okumayi da kapatmistim, mevcut testler yakaladi.)
- [x] **B5-8 (P2)** - NPCSKILLSAVE okunmuyordu. (YAPILDI: dunya okunurken esik altindaki
  NPC skilleri dusuruluyor - referansta FixWeirdness'in yaptigi; oyuncuya dokunulmuyor,
  sifir her seyi korur.)

**Canli shard etkisi:** o ini `NOWEATHER=0` ve `NPCSKILLSAVE=100` diyor; yani hava acik
kalir ve 10.0 altindaki NPC skilleri dusurulur - ikisi de shard'in kendi tercihi.

**Kapanis:** tam suite **3.285 basarili / 0 basarisiz** (+2).

**IS-5 - pakete konan ini duzeltildi (8 Eylul 2026):** Bu turda baglanan anahtarlarin
`config/sphere.ini` icindeki YER TUTUCU degerleri canli hale gelmisti ve bircogu
referansin tersini soyluyordu (teleport efekt/ses 0, MAXHOUSESGUILD 0, BACKPACKOVERLOAD 0
- ustelik aciklamasi 0/1 anahtari diyordu -, FLIPDROPPEDITEMS 0, NPCCANFIZZLEONHIT 1).
Degerler referansinkine cekildi, aciklamalar duzeltildi, `[UYGULANMADI]` etiketleri
`[ÇALIŞIYOR]` oldu, eksik olan MAXITEMCOMPLEXITY eklendi. Yeni test pakete konan ini'yi
motorun okuduguna karsi dogruluyor.

**Uyari - CANLI sunucu ini'si:** `C:\sphereNetServer\sphere.ini` ayni yer tutuculari
tasiyor (TELEPORTEFFECT*/SOUND* 0, MAXHOUSESGUILD 0, BACKPACKOVERLOAD 0,
FLIPDROPPEDITEMS 0, NPCCANFIZZLEONHIT 1, MAXITEMCOMPLEXITY satiri yok). O dosya depoda
degil; kullanici onayi olmadan degistirilmedi.

**IS-5 devami - MAXPOLYSTATS'in tuketicisi yazildi (8 Eylul 2026):**

- [x] **B5-9 (P2)** - Polymorph yalnizca govdeyi degistiriyor, formun statlarini hic
  uygulamiyordu; bu yuzden MAXPOLYSTATS'in sinirlayacagi bir sey yoktu. (YAPILDI:
  MAGICF_POLYMORPHSTATS acikken formun STR/DEX'i aliniyor, degisim iki yonde de
  MAXPOLYSTATS ile sinirli, delta buyu hafizasinda - form kalkinca geri aliniyor.
  Deger bildirmeyen tanim o stata dokunmuyor.)
- Yan urun: `DefinitionLoader.GetCharDefByBody` - tanimi cizildigi govdeden bulan indeks.

**Kapanis:** tam suite **3.290 basarili / 0 basarisiz** (+4). Iki ini dosyasina da
MAXPOLYSTATS satiri eklendi.

### Bolge tetikleyicisinin nesne kimligi (8 Eylul 2026)

Kaynak: IS-2c turunda statik gozlem olarak kaydedilmisti.

- [x] **BT-1 (P2)** - Bolge/oda @Enter/@Exit/@Step scriptleri KARAKTER uzerinde
  calisiyordu; bolge duzeyi her okuma yuruyenden yanitlaniyordu. (YAPILDI: script bolge
  /oda nesnesi uzerinde calisiyor, karakter SRC olarak kaliyor - referansin
  OnRegionTrigger sozlesmesi. EVENTSREGION isleyicileri de ayni nesneye baglandi.)

**Kanit:** yeni `RegionTriggerObjectParityTests` tetikleyici icinden `<TAG.owner>`,
`<NAME>` ve `<SRC.NAME>` okuyor; duzeltme gecici geri alindiginda test dusuyor.

**Kapanis:** tam suite **3.291 basarili / 0 basarisiz** (+1).

### IS-17 - Kabin kendi agirlik siniri (9 Eylul 2026)

IS-16'nin tamamlayicisi. Duz 400 tavani kaldirinca siradan bir kabi sinirlayacak tek
sey referansin kullandigi deger kaliyordu: kabin kendi `MODMAXWEIGHT`'i. Referansta bu
ORTAK NESNE TABANINDA (`OC_MODMAXWEIGHT`, CObjBase.cpp:1104), yani hem karakter hem
esya yanitlar; bizde yalnizca karakterde vardi.

- [x] **CW-1** - `Item.ModMaxWeight` + `MODMAXWEIGHT` oku/yaz.
- [x] **CW-2** - Kap agirlik kontrolu once kabin kendi degerine bakiyor; ini'deki genel
  deger yalnizca kap sifir derse devreye giriyor (ikisi de sifir = sinir yok).
- [x] **CW-3** - Kayda yaziliyor (yoksa restart'ta scriptin kurdugu her sandik
  sinirsizlasirdi).

Test: `ContainerWeightLimitTests` (4). Tam suite 3.364.

### Property OKUMA olcumu - IKINCI deneme de sonucsuz

IS-16'da "dogru arac calisma zamani olcumu" diye kaydedilmisti; denendi. `ExpressionParser`
`DebugUnresolved`+`DiagnosticLogger` ile paketteki 1.158 farkli nesne-property okumasi
motora cozduruldu. **Sonuc yine kullanilamaz:** en cok "cozulemedi" diyen basliklar
`SRC` (287), `DLOCAL` (129), `DEF0` (81), `LINK`, `CTAG0`, `NEW`, `ACT`, `TOPOBJ` -
hepsi bench'te BAGLAM olmadigi icin. Yani olcum motorun eksigini degil, kurulumun
yoksullugunu olcuyor.

**Sonuc:** bu ayak icin dogru olcum, ifadeleri tek tek cozdurmek DEGIL; gercek bir
dunyada gercek trigger'lari calistirip (`SRC`, `ARGO`, dialog local'leri yerinde) o
sirada cikan uyarilari toplamaktir - yani bir oyun-akisi kosum tezgahi. Bu, ayri ve
buyuk bir is; iki kez denenip iki kez sonucsuz kaldigi buraya yazildi ki ucuncu kez
statik/baglamsiz denenmesin.

### IS-16 - Uydurma-deger backlog'unun kalan iki olculebilir maddesi (9 Eylul 2026)

`project_invented_values_audit_backlog` icindeki "KALAN/ERTELENEN" listesinden, kosulu
OLCULEBILIR olan iki madde kapandi.

**1. Fallback gathering ekonomisi.** Backlog'un kendi kosulu: "pack REGIONRESOURCE
tanimliyorsa olu kod, silinebilir". Olcum: canli pakette 31 REGIONRESOURCE, 46
REGIONTYPE; ucunun de (t_rock 4, t_water 2, t_tree 1) RESOURCES satiri var. Ayrica
`GatheringEngine` eslesme bulamazsa global `FindRegionTypeByFilter`'a dusuyor - yani
paket yuklenmisken toplama HER ZAMAN "Handled". Dolayisiyla
`ActiveSkillEngine`'deki uc fallback (demir cevheri 0x19B9 rand(1..3), balik 0x09CC,
kutuk 0x1BDD rand(1..5) + kendi marker havuzu) olu koddu ve silindi (38 satirlik
yardimci cift dahil). Tam suite degismeden gecti (3.360) - hicbir test ona dayanmiyordu.

**2. ContainerMaxWeight 400.** Backlog'da "SX karsiligi dogrulanmadi" diye duruyordu;
dogrulandi ve **karsiligi yok**: referansta kabin siniri kendi `m_ModMaxWeight`'i
(varsayilan 0) ve kontrol `iMaxWeight > 0` ile atlaniyor (CItemContainer.cpp:906-916).
Global tavan yalnizca oyuncunun cantasi icin var (tasima + BACKPACKOVERLOAD, bizde
IS-5'te dogru baglandi). Varsayilan 0'a alindi; ini anahtari isteyen shard icin duruyor.
**Banka sinirina dokunulmadi** - `g_Cfg.m_iBankWMax` referansta gercek.

**Ayrica dogrulandi:** paketlerin `MODMAXWEIGHT` kullanimi (4 yer) CHARDEF'te, yani yuk
hayvanlarinda; karakter tarafi bizde zaten destekli (`Character.MaxWeight`).

### Property OKUMA taramasi - sonucsuz (ayni tur)

Script yuzeyinin ucuncu ayagi (trigger = kanca, verb = komut, property = okuma) ayni
yontemle olculmeye calisildi: 1.881 dosya, 52.615 `<...>` okumasi. **Statik olcum bu
ayakta guvenilir aday uretmiyor** ve iki ayri kusur bunu gosterdi:

1. Ad-uzayi yutulmasi: `<def.tcolor_red>` gibi okumalarda son parca alininca paketin
   kendi sabit tablosu "eksik property" gibi gorundu (1.236 aday). Ad-uzayina gore
   filtrelenince 710'a dustu.
2. Argumanli anahtarlar: `<SRC.FINDLAYER.layer_hair>` gibi okumalarda property ORTA
   parca; son parcayi almak `FINDLAYER`i (uygulanmis) eksik gosterdi.

Kalan liste hala paketin kendi sozlugu agirlikli. Bu ayak icin dogru arac motorda ZATEN
var: `ExpressionParser.DebugUnresolved` + `DiagnosticLogger` (`.SCRIPTDEBUG`), yani
CALISMA ZAMANI olcumu. Statik tarama tekrar denenmemeli.

### IS-15 - Verb uzun kuyrugu: paket tarafindan olcum (9 Eylul 2026)

IS-8'deki trigger olcumunun verb karsiligi. Yontem ayni: PAKET tarafindan sor, motorun
kendi tablosuna degil.

**Olcum:** 1.881 script dosyasi, 14.223 ifade satiri, 314 farkli ifade basligi.
Motorda hicbir izi olmayan: 121.

**Siniflandirma (onemli):** 121'in neredeyse tamami shard'in kendi `[FUNCTION]`
bloklari (`F_SHADOW_CURSE` 63, `F_DUNGEON_CHEST_ADDITEM` 48, `MAKE_TEKRAR` 16 ...).
Bunlar FunctionResolver yolundan cozulur ve motor verb'i DEGILDIR - hafizadaki
"script functions != intrinsics" kurali tam bu. Referans tablolarina
(`tables/*.tbl`) karsi kontrol edildiginde gercek verb sayisi **iki**:

| Verb | Kullanim | Durum |
|---|---|---|
| `MESSAGE` / `MSG` | 330 | referansta CObjBase verb'i - **yoktu** |
| `ADDCIRCLE` | 9 | referansta CItem verb'i - **yoktu** |

`MESSAGE` bizde yalnizca `ClientScriptConsoleHandler`'da bir konsol komutuydu; nesne
uzerinde cagrilinca `TryExecuteCommand` false donuyordu (calisma zamaninda dogrulandi:
`SAY`/`EMOTE` true, `MESSAGE`/`MSG` false).

- [x] **VB-1** - `ObjBase` verb'i `MESSAGE`/`MSG` + `OnObjectMessage` kancasi. Kaynak
  karakter varsa YALNIZ ona (upstream `ObjMessage`), yoksa nesne uzerinde yayin
  (`UpdateObjMessage`). TALKMODE_ITEM.
- [x] **VB-2** - `ADDCIRCLE <cember>[,<altindakiler>]`, spellbook disina red.

Test: `ObjectMessageVerbTests` (5) + `AddCircleVerbTests` (5). Tam suite 3.360.
Olcum yeniden kosuldu: 121 -> 120 -> 119 (iki verb listeden dustu).

### IS-14 - SKILLUSEQUICK (9 Eylul 2026, PLAN-304 kuyrugu)

IS-13'te "sorgu gorunumlu ama yan etkili" diye ertelenen madde. Sozlesme
(CChar.cpp:2802 -> CCharSkill.cpp:542):

| Arguman | Anlam | Not |
|---|---|---|
| 1 | beceri adi | `SkillNames` ile cozulur |
| 2 | zorluk | ifade |
| 3 | can egrisi | **TERS**: sifir degilse egri KAPALI |
| 4 | zorla | SCRIPTED beceride bile zar at |

`fAllowGain` referansta sabit true. `SkillEngine.UseQuick`'e `forceCheck` eklendi
(SCRIPTED reddini atlar, CCharSkill.cpp:553). Ikiden az arguman -> anahtar YANITSIZ
(referans `return false`), sifir degil.

- [x] **SQ-1** - `SKILLUSEQUICK` property + `OnSkillUseQuickProperty` kancasi.
- [x] **SQ-2** - `UseQuick(..., forceCheck)`.

Test: `SkillUseQuickPropertyTests` (7). Tam suite 3.350.

**Yerlestirme notu:** bilesik anahtar, genel skill-adi aramasindan ONCE denenmeli;
sonraya konursa "SKILLUSEQUICK.x,y" dizesi baska bir dal tarafindan yutuluyor.

**SKILLTEST kapsam disi:** referansta var (`SkillResourceTest`), paketlerde 0 kullanim;
yanitlamak icin kaynak-listesi ayristiricisini disa acmak gerekir. Kayitli.

### Olculup bosluk CIKMAYAN alanlar (ayni tur, ayni gun)

Zaman kaybini tekrar etmemek icin: bu turda iki tarama daha yapildi ve **ikisi de temiz
cikti**.

1. **"Yazilip hic okunmayan durum" taramasi** (MODSTR kalibini genellemek icin):
   Character'da 119, Item'da 69 script-yazilabilir anahtar tuketicilerine karsi tarandi;
   4 aday cikti, **dordu de yanlis pozitif** (FleeStepsMax, HomeDist, BaseStorage,
   FACTION_SPECIES - hepsi public property ya da tag uzerinden okunuyor). Taramanin
   kapsami sinirli (yalnizca uye yazan anahtarlar), ama MODSTR'in istisna oldugunu
   gosteriyor.

2. **Olum aninda imlecteki esya** (PLAN-406 listesinden): ilk sonda esyayi
   `Layer.Dragging`'e koyup kaybettigini gosterdi, ama bu kurulum GERCEK degil - motor
   surukleme durumunu `DRAGGING` TAG'inde tutuyor. Dogru kurulumla esya cesede giriyor
   ve zaten `DeathInventoryEdgeTests` bunu sabitliyor. Onceki bir dalgada bulunmus ve
   duzeltilmis.

### IS-13 - CANMAKE / CANMAKESKILL (9 Eylul 2026, PLAN-304)

Plan maddesi "CANCAST zaten mevcut: yeniden yazmak yerine kapsami tamamla" diyordu.
Olcum bunu dogruladi: `CANCAST` var ve calisiyor (Character.cs:4045, paketlerde 66
kullanim). `CANMAKE`/`CANMAKESKILL` ise hic yoktu; paketlerde 8 kullanim.

**Referans sozlesmesi:** ikisi de `Skill_MakeItem(id, uid, SKTRIG_SELECT)`
(CChar.cpp:2787). Icerde sira: `SkillResourceTest(SKILLMAKE)` -> `fSkillOnly` ise
BURADA true doner -> degilse `ResourceConsume(BaseResources, test)` (CCharSkill.cpp:913).
Yani CANMAKESKILL = beceri + SKILLMAKE gereksinimleri; CANMAKE = onlar + malzeme.

**Calisma yeri bilerek disarida:** referans ocak/ates kontrolunu becerinin kendi
asamasinda yapar, SELECT'te degil. `CanCraft`'a `checkWorkSite` parametresi eklendi ve
sorgu yolu false geciyor; uretimin kendisi eskisi gibi ocagi ariyor.

- [x] **CM-1** - `CraftingEngine.CanCraft(..., skillOnly, checkWorkSite)`.
- [x] **CM-2** - `Character.OnCanMakeCheck` + `CANMAKE`/`CANMAKESKILL` property'leri
  (nokta ya da bosluk ayirici, defname veya sayisal id).

Test: `CanMakeQueryParityTests` (6). Tam suite 3.343.

**Not:** `SKILLTEST` ve `SKILLUSEQUICK.<skill>,<zorluk>` referansta ayni bolgede duran
iki sorgu daha; `SKILLUSEQUICK` paketlerde 2 kez geciyor. Bu turda ele alinmadi -
`Skill_UseQuick`'in yan etkileri (deneme sayilir, beceri kazanci tetiklenir) sorgu
gorunumlu ama sorgu degildir; ayri ve dikkatli bir is.

### IS-12 - Esya sesleri (9 Eylul 2026, PLAN-305)

Plan maddesinin kendi ifadesi: "Item ses property'lerinin varligi ile ses calmanin
varligini ayir." Ayrildi ve ikisi de eksikti.

**Olcum:** `DROPSOUND`/`EQUIPSOUND` referansta gercek item property'leri
(CItemBase_props.tbl:26/32); bizde ikisi de yok. Elimizdeki hicbir paket bunlari
yazmiyor (olcum 0), yani oncelik sinyali sifir - AMA davranis tarafi script'e bagli
degil ve orada iki gercek bosluk vardi:

1. **Kusanma sesi hic calinmiyordu.** Referans gorunur katmanda 0x057 calar
   (CCharAct.cpp:3355). Bizde hicbir yerde ses yoktu.
2. **Birakma sesi tek sabitti.** Referansin sirasi: TUR tablosu -> DROPSOUND anahtari ->
   yedek (`ustune` 0x057 / `yere` 0x042). Bizde yalniz 0x042 vardi ve "ustune dustu"
   ayrimi yoktu.

**Uydurma deger temizligi:** altin sesleri bizde 0x2E4-0x2E6 ve bantlama 1/<6/fazla
idi; referans 0x035/0x032/0x036/0x037 ve 1/2/3-4/fazla. Referansa alindi
(uydurma-deger denetim backlog'undan bir madde daha).

- [x] **SN-1** - `Item.GetDropSound(ontoSomething)` referansin tam sirasiyla.
- [x] **SN-2** - `Item.GetEquipSound()` + `Item.IsVisibleLayer` ve kusanmada calinmasi.
- [x] **SN-3** - `DROPSOUND`/`EQUIPSOUND` instance -> ITEMDEF cozumlemesi (GetDefKey).

Test: `ItemSoundParityTests` (13). Tam suite 3.337.

**Test notu:** tanimlar constructor'da degil TEST GOVDESINDE yuklenmeli - paylasilan
statik sifirlama (`ResetEngineStatics`) ikisinin arasinda kosuyor ve constructor'da
yuklenen tanimlari siliyor. Ilk yazimda 7 test bu yuzden dustu.

### IS-11 - Stat modifier ailesi ve OSTR (9 Eylul 2026, PLAN-303)

Referans modeli: her stat icin BIR temel ve BIR modifier. `Stat_GetAdjusted` = temel +
mod (CCharStat.cpp:143); modifier hem scriptin MODSTR'ini hem equip aninda eklenen
BONUSSTR'i tasir (CCharAct.cpp:3382). `STR` okumasi ayarlanmis degeri, `OSTR` ciplak
temeli verir (CChar.cpp:3146/3139) ve HER IKI ad da yazildiginda temeli yazar (:3641).

**Bulgu 1 (P1, veri kaybi) - OSTR golge alani.** Bizde `_oStr` ayri bir alandi. Stat
degisir degismez bayatliyor; kayitta `STR` satiri once, `OSTR` sonra yazildigi ve
yukleyici O-adini da temele uyguladigi icin yuklemede golge kazaniyordu. Olcum:
110/111/112 kaydedip 100/100/100 yukleniyor. **Klasik kayittan gelen her karakter, yani
56T'deki herkes, her restart'ta antrenmanini kaybediyordu.** `OStr/ODex/OInt` artik
temelin takma adi; kayit O-satirlarini yazmiyor (STR zaten temeli soyluyor), yuklemede
kabul etmeye devam ediyor.

**Bulgu 2 (P2, sessiz no-op) - MODSTR yazilip hic okunmuyordu.** `_modStr/_modDex/
_modInt` alanlarinin TEK okuyucusu kendi property'leriydi: ne etkin stata giriyor, ne
kaydediliyordu. Canli paket 83 kez yaziyor. Artik `CombatEngine.EffectiveStr/Dex/Int`
iceriyor (dolayisiyla hasar, tasima, REQSTR, skill, ekran) ve kayitta temelden once
yaziliyor.

**Sozlesme degisikligi:** `<STR>` artik ayarlanmis degeri donuyor. `SourceXWave263Tests`
eski okumayi sabitlemisti; gerekcesi "equip aninda alan degistirilmiyor" idi - o karar
dogru ve duruyor, ama OKUMA sozlesmesi ayri bir sorudur ve referansta ayarlanmis
degerdir. Test referansa gore guncellendi (STR=80, OSTR=50).

- [x] **SM-1** - OSTR/ODEX/OINT temelin takma adi. Test: `StatModifierParityTests` (7).
- [x] **SM-2** - MODSTR/MODDEX/MODINT etkin stata katiliyor + kaydediliyor.
- [x] **SM-3** - `<STR>` ayarlanmis, `<OSTR>` temel.

Uc duzeltme tek tek geri alinip dogrulandi: modifier 5, OSTR 2, mod-kayit 1 test dusuyor.

**Kapsam disi birakildi:** `MODMAXHITS/MODMAXMANA/MODMAXSTAM/MODMAXFOOD` referansta var,
bizde hic yok - ama elimizdeki hicbir paket/kayit bunlari kullanmiyor (olcum: 0). Havuz
tarafinda ayni modeli kurmak ayri bir is.

### Altyapi - UOP harita gecici dosyasi sizintisi

Test paketi calistigi makinede `%TEMP%` 120 GB'a ulasmisti: 1433 adet
`spherenet_map_*.tmp`, her biri ~84 MB. `UopMapReader.Dispose` dosyayi siliyor ama
cagiranlar dispose etmiyor. Dosya artik `FileOptions.DeleteOnClose` ile aciliyor
(mapping icin `FileShare.Delete`), yani Dispose'a hic ulasilmasa bile isletim sistemi
geri aliyor. Tam paket kosusundan sonra artik dosya sayisi: 0.

### IS-10 - Yansima ailesi tek hasar girisine baglandi (9 Eylul 2026)

IS-9'da kaydedilen bulgu kapandi. Referansta hasar TEK kapidan gecer
(`CChar::OnTakeDamage`); bizde iki giris var (`ResolveAttack` = yakin dovus,
`ApplyScriptDamage` = DAMAGE verb) ve yansima ailesinin ucu de yalniz birincisindeydi.

Aile ortak adimlara cikarildi ve iki girisin ikisi de cagiriyor:
- `IsReflectableBlow(kaynak, hedef, tur)` - referansin iki kapisi: fiziksel darbe turu
  (`DAMAGE_HIT_BLUNT|PIERCE|SLASH`, CCharFight.cpp:946) ve hedeften farkli bir kaynak
  (:918).
- `ApplyReactiveArmor(...)` - darbe DUSMEDEN once (referans once azaltir).
- `ApplyBloodOathAndSuitReflect(...)` - darbe dustukten sonra.

**Kapsam olcumu (onemli):** ilk bakista "butun hasar yollari" sanmistim; referans
kaynagini okuyunca kapsam daraldi. Aile fiziksel darbeye ozel, yani BUYU hasari
referansta da sekmez. Tuzak/alan hasari `pSrc = nullptr` ile cagrildigi icin yine
disarida. Bizdeki 11 hasar uygulama noktasindan yalnizca DAMAGE verb yolu gercek
boslugtu.

- [x] **RF-1** - `ApplyScriptDamage` karakter dalinda aile calisiyor. Test:
  `ScriptedDamageReflectTests` (6); baglanti gecici geri alinip 4 testin yakaladigi
  dogrulandi. Tam suite 3.317.

**Not:** bizdeki 11 ayri hasar uygulama noktasi (traps, fields, spells, combat, verb)
referansin tek `OnTakeDamage` kapisiyla ortusmuyor. Bu dalgada davranis paritesi
saglandi; yapisal birlestirme ayri bir is olarak durmali.

### IS-9 - PLAN-206'nin dort gercek boslugu kapandi (9 Eylul 2026)

Olcumun cikardigi dort trigger uygulandi. Sozlesmeler referanstan alindi:

| Trigger | Nerede | Sozlesme |
|---|---|---|
| `@RegionResourceFound` | karakter | ARGO = damar isaretcisi; RETURN 1 -> miktar 0 |
| `@ResourceFound` | REGIONRESOURCE tanimi | ayni argumanlar, donus degerini o belirler |
| `@RegionResourceGather` | toplayan | ARGN1 = miktar, LOCAL.ResourceID; RETURN 1 -> hicbir sey |
| `@HitReactive` | savunan | LOCAL.Sound/EffectID/Damage/ReflectDamage/ReduceDamage/DamageType |

**Iki trigger tek donus degeri paylasiyor** (hem found hem gather ciftinde): sonraki
oncekinin uzerine yazar ve her atama `IsTrigUsed` ile korunur. Bunun icin
`TriggerDispatcher.IsTriggerNameUsed(ad)` eklendi - referansin IsTrigUsed'i gibi GLOBAL
bir soru, ve onbellek kurulmadan once "bilinmiyor" diye ACIK dusuyor (kancali bir
trigger'i asla "kullanilmiyor" diye bildirmemeli).

**Reaktif zirh yeniden kuruldu (3 kusur):**
1. Yansiyan pay `damage / 4` sabitiydi - referansta yok. Artik buyu taniminin EFFECT
   egrisinden, buyu yapilirken hesaplanip saklaniyor (`Character.ReactiveArmorPercent`,
   Blood Oath'taki ayna deseniyle; undo/restore dahil).
2. Darbe yansitiliyor ama AZALTILMIYORDU. Referans once dusurup sonra yansitir.
3. Iki karelik mesafe sarti yoktu.

Yansima, kalan hasar sifir olsa bile calisir (tam sogurma da geri seker) - blogu
`damage > 0` kapisinin icine koymak ilk denemede tam bunu bozdu, test yakaladi.

- [x] **TG-3** - dort trigger + reaktif zirh duzeltmesi. Test:
  `ResourceAndReactiveTriggerTests` (12). Korkuluk listesi bosaldi.

**Yeni kayitli bulgu (degistirilmedi):** reaktif zirh, Blood Oath ve REFLECTPHYSICALDAM
yalnizca `ResolveAttack` (yakin dovus) yolunda. Referans bunlari `OnTakeDamage`'a koyar;
DAMAGE verb'i (`ApplyScriptDamage`) oradan gectigi icin scriptli hasar da yansimali
olmali. Ucu birden ayni yerde eksik - tek bir "yansima ailesi" isi olarak ayri ele
alinmali.

### PLAN-206 - Trigger uzun kuyrugu: paket tarafindan olcum (8 Eylul 2026)

Plan maddesinin kendi sarti: "Eksik trigger adlarini enum adi uzerinden degil Source-X
isim/alias/baglam eslemesiyle cikar." Oyle yapildi.

**Olcum 1 - referans tablosu:** `tables/triggers.tbl` 248 trigger. Motorun kaynaginda
hicbir izi olmayan 18 ad var; elimizdeki hicbir paket bunlardan birini bile kancalamiyor
(HOUSEDESIGNCOMMITITEM yalnizca upstream ornek scriptlerinde). Yani bu 18, canli bir
bosluk degil.

**Olcum 2 - paket tarafi (asil sinyal):** 910 script dosyasi, 10.166 `ON=@Ad` kancasi,
123 farkli ad. Dokuz ad hicbir yere ulasmiyor:

| Ad | Kullanim | Durum |
|---|---|---|
| `@ResourceFound` | 30 | referansta VAR (CWorldMap.cpp:159) - bosluk |
| `@RegionResourceGather` | 3 | referansta VAR (CCharSkill.cpp:1035) - bosluk |
| `@RegionResourceFound` | 2 | referansta VAR (CWorldMap.cpp:157) - bosluk |
| `@HitReactive` | 2 | referansta VAR (CCharFight.cpp:965) - bosluk |
| `@npcmount`, `@NPCDisMount`, `@move`, `@skilluse`, `@statgain` | 23 | referansta YOK - olu script |
| `@TameAbort`, `@SpellStart`, `@PartyJoin` | 3 | referansta YOK - olu script |

**Elenen yanlis alarmlar (onemli):** ilk statik gecis 19 aday uretti; dogrulamada
cogunun ates ettigi gorildu. Skill asamalari kisa bolum adlariyla
(`GetSkillSectionStage`: SkillPreStart -> "PreStart"), bolge/oda ve [SPELL] asamalari
duz adla (`FireRegionEvents(region, "RegPeriodic", ...)`), `@item<Ad>`/`@char<Ad>`
aynalari ise string birlestirmeyle (`"item" + trigName`) atesleniyor. Hicbiri bir enum
degerinden gecmedigi icin "enum uyeleri ates ediliyor mu" olcumu bunlari goremez.

- [x] **TG-1** - `TriggerDispatcher.DispatchableTriggerNames`: motorun gercekten
  ulasabildigi ad kumesi (char/item adlari + iki ayna + skill asamalari + bolge/buyu
  asamalari). Olcum artik kaynak koda regex atmiyor, motora soruyor.
- [x] **TG-2** - `ScriptPackTriggerCoverageTests`: makinede bulunan paketleri tarar,
  her kancayi bu kumeye karsi dogrular, paket yoksa atlar. Kayitli bosluk listesi
  kendini bakim eder: bir bosluk kapaninca test onu listeden silmeni ister.

**Sirada (bosluk kapatma):** dort gercek trigger. Kaynak ucusu (`@ResourceFound` ->
REGIONRESOURCE tanimi uzerinde, `@RegionResourceFound` -> karakter uzerinde, ARGO =
kaynak biti, RETURN 1 = miktar 0) ile `@RegionResourceGather` (ARGN1 = miktar,
LOCAL.ResourceID yazilabilir, RETURN 1 = hicbir sey uretme) tek bir zincir; `@HitReactive`
ayri (LOCAL.Sound/EffectID/Damage/ReflectDamage/ReduceDamage/DamageType geri okunur).

### PLAN-102/103 - Kopya ve kayit dongusunde havuz degerleri (8 Eylul 2026)

13J raporunun dort bulgusu (temel/etkin havuz, oz referans, dogrudan DUPE, kap ici
konum) kodda kapali; denetlendi. Raporun kendi kabul ettigi eksik parca ise KAYIT
DONGUSUYDU: "sismis temel degerin kayda tasinmasi koddan gorulen risktir, gercek kayit
deneyi yapilmadi."

Deney yapildi ve **ters yonde** bir hata cikti: temel deger sismiyor, MEVCUT havuz
kaybediliyordu. BONUSHITSMAX kiyafetli karakter 120/120 kaydedip 100/120 yukleniyordu.

- [x] **HV-1 (P1)** - Atama ile oynanis degisimi ayrildi. Referansta `HITS=` ->
  `Stat_SetVal` (ust sinir YOK, yalniz negatif reddedilir, CChar.cpp:3901 ->
  CCharStat.cpp:157); hasar/iyilestirme/yenilenme -> `UpdateStatVal` (ayarlanmis
  maksimuma kirpar, CCharAct.cpp:753). SphereNet'te tek kirpan setter ikisini de
  yapiyordu. `SetHitsRaw`/`SetManaRaw`/`SetStamRaw` eklendi; `TrySetProperty` (kayit
  yukleme ve script yaziminin ortak yolu) bunlari kullaniyor. Yan fayda: kirpan setter
  her dususu "hasar aldi" sayip skill/meditasyon kesintilerini tetikliyordu - diskten
  okunan karakter bunu hak etmiyor.
- [x] **HV-2 (P1)** - Yukleme ekipmani iki kez giydiriyordu (esyanin kendi kaydi +
  karakterin `EQUIP[n]=` bagi). Ikinci giydirme once cikariyor; cikarma
  `Stat_AddMaxMod` paritesi geregi havuzu dusen maksimuma kirpiyor. Katmanda zaten
  duran ayni esya icin giydirme atlaniyor.
- [x] **HV-3 (P3)** - Kayit sirasi referansa alindi: `MAX*` satirlari `HITS/MANA/STAM`
  oncesinde. Referans bunu acikca vurguluyor ("this is VERY important",
  CChar.cpp:4250). Bizde gizli kalmisti: `STR=` zaten `_maxHits`'i kurdugu icin sonraki
  `MAXHITS=` cogu kayitta no-op oluyor ve kirpma tetiklenmiyordu; temel maksimum
  STR'den farkli oldugunda tetiklenir. Testle sabitlendi.

Testler: `PoolRoundTripParityTests` (6). Uc duzeltme tek tek geri alinip dogrulandi:
atama 3, giydirme 3, sira 1 test dusuyor.

### Ham `_type` kapilari - olcum sonucu (8 Eylul 2026)

Kaynak: IS-2b/2c turlarinda "ertelendi" diye kaydedilen madde.

**Bulgu: kalan ham okuyucular icin degisiklik GEREKMIYOR.** Sebep zincirini olcerek
dogruladim:

1. Fabrika yolu (`ItemDefHelper.ApplyInstanceMetadata`) turu zaten kuruyor.
2. Klasik kayit yolu TYPE satiri yazmaz (56T'de 76 bin esyada yalniz 721 TYPE satiri
   var), ama yukleyici yuklemeden sonra `MaterializeDefinitionType()` ile ham turu
   tanimdan dolduruyor.
3. `WorldInvariantAuditor.AuditType` tam bu iraksamayi kontrol ediyor ve gercek 56T
   dunyasinda **0 anomali** veriyor.

Gercek hata SADECE kayit OKUNURKEN gecerli olan pencerede vardi (property get/set
yuzeyi: harita pini, kitap sayfasi, gemi anahtarlari) - o da `Item.EffectiveType` ile
kapatilmisti.

- [x] **HT-1 (P2)** - Gercek veri kontrolu artik butun dunya degismezlerini assert
  ediyor (once yalnizca yukleyip anahtar sayiyordu). Boylece "ham tur okuyuculari
  dogru" iddiasi her koşuda canli veride sinaniyor.

**Kalan risk (kayitli, degistirilmedi):** calisma aninda `CreateItem()` + `BaseId` ile
kurulup turu ACIKCA verilmeyen esya; denetleyici yalnizca yukleme sonrasi calistigi icin
boyle bir esya ancak kaydedilip yeniden yuklendiginde yakalanir. Uretimdeki bu tur
yerlerin tamami turu hemen ardindan kendisi ayarliyor.

### IS-4 - nesne olusturma/kopyalama giris noktalari tablosu (7 Eylul 2026)

Kaynak: port plani IS-4 / PLAN-101. Amac: ayni isi yapan girislerin ortak ve farkli
davranislarini tabloya dokmek; farki ya kapatmak ya da bilincli sapma olarak yazmak.

| Giris | @Create | NEW | ACT | Miktar/sayi | Yerlestirme | Basarisizlik |
|---|---|---|---|---|---|---|
| SERV.NEWITEM (ITEMDEF) | evet | isin SONUNDA | hayir (sunucuya yonelik) | Sphere ifadesi, sifir korunur | parent alani: kap / katman / pack | NEW temizlenir, nesne silinir |
| NEWITEM (nesne uzerinde) | evet | evet | evet (cagiranin ACT'i) | ayni | ayni | ayni |
| SERV.NEWITEM (TEMPLATE) | her satirda | recetenin sonucu | - | dis miktar sonuca uygulanir | recete + parent | NEW temizlenir |
| SERV.NEWNPC | CHARDEF @Create | evet | - | - | - | NEW temizlenir |
| NEWDUPE | HAYIR (kopya) | evet | evet | - | ust nesnenin YANINA | kaynak yoksa NEW temizlenir |
| DUPE (esya) | HAYIR | evet | **evet (bu tur eklendi)** | Sphere argumani; ust duzeyde MAXITEMCOMPLEXITY | ust nesnenin yanina | yerlesemezse kopya silinir |
| DUPE (karakter) | HAYIR | **hayir - referans da oyle** | hayir | arguman newbie sozlesmesini secer | kaynagin konumu | - |
| spawn | evet | - | - | PILE (yiginlanabilirse) | spawn noktasi cevresi | tur atlanir |
| TEMPLATE satiri | evet | - | - | satir miktari + R# sansi | gecerli kap | satir hicbir sey uretmez |
| yigin bolme | HAYIR | - | - | bolunen adet | ayni kap | - |

- [x] **B4-1 (P2)** - Esya DUPE'u kopyayi cagiranin ACT'ine yazmiyordu. (YAPILDI:
  `CreateDupeItem` sozlesmesi - NEW ve ACT kopyayi gosterir, birden fazlada sonuncusu.)
- [x] **B4-2 (P2)** - DUPE sayisi 1000'e sabitlenmisti; motorun kendi uydurdugu sayi.
  (YAPILDI: `MAXITEMCOMPLEXITY` ini ayari eklendi, varsayilan 25 - referansin degeri;
  yalniz UST DUZEY nesnede uygulanir, kap icindeki kopya sinirsiz.)

**Bilincli sapma olarak DOGRULANANLAR (degistirilmedi):** kopya hicbir yolda @Create'i
tekrar calistirmaz; karakter DUPE'u NEW/ACT ayarlamaz (CHV_DUPE dogrudan DupeFrom
cagirir); SERV. onekli bicim ACT'e dokunmaz, ciplak bicim dokunur (CScriptObj.cpp:1383).

**Kapanis:** tam suite **3.278 basarili / 0 basarisiz** (+5).

**Acik kalan:** vendor restock ve loot yollarinin NEW/ACT sutunlari bu turda canli
denenmedi (tabloda "-" ile isaretli, "yok" degil "olculmedi" demektir).

### IS-3 - kaydin sabit noktasi (7 Eylul 2026)

Kaynak: port plani IS-3 / PLAN-107.

- [x] **B3-1 (P2)** - Kendi maksimum cani olmayan karakter `MAXHITS=0` olarak
  kaydediliyor, yuklemede setter onu 1'e yuvarliyordu; yani ikinci kayit ilkinden farkli
  oluyordu (kayit sabit noktaya ulasmiyordu). (YAPILDI: sifir maksimum yazilmiyor.)

**Yeni test - `SaveRoundTripParityTests`:** kaydet -> yukle -> kaydet, iki kaydi satir
satir karsilastir. Iki dunya: motorun API'siyle kurulan (karakter statlari/skilleri/
tag'leri/EVENTS, giyili esya, ic ice kap ve konumlari, timer, MORE/LINK) ve KLASIK
kayitlardan yuklenen (lonca listesi, gemi ambari/iskelesi, yapi bolgesi, oldurme
sayaclari, yeniden adlandirilmis skill).

**Test kalitesi notu:** her seyi kaybeden kayit da sabit noktadir; bu yuzden klasik
turda once cevrilen verinin kayitta BULUNDUGU dogrulaniyor (GUILD.MEMBERS, SHIP.HOLD,
SHIP.PLANKS, REGION.TAG.OWNER, KILLS=5, Spellweaving=1000, KILLSNPC).

**Saat normalizasyonu:** TIME/TIMER/TIMERMS/TIMERD/CREATE degerleri iki kayit arasinda
mesru olarak ilerler; karsilastirma bu satirlarin degerini degil varligini olcer.

**Kapanis:** tam suite **3.273 basarili / 0 basarisiz** (+2).

### IS-2c - yapinin bolgesi (7 Eylul 2026)

- [x] **B2c-1 (P1)** - Klasik kayittan gelen 93 yapinin hicbir bolgesi yoktu: bolge
  gerceklestirme EV kaydina bagliydi, ev kaydi da bu motorun kendi sahiplik tag'ini
  istiyor. Sonuc: Safe/NoBuild bayraklari, @Enter/@Step scriptleri ve bolge tag'leri
  hicbir sey yapmiyordu. (YAPILDI: `RealizeMultiRegion` multi'nin kendisine bagli -
  referansta da oyledir, CItemMulti.cpp:191/571. Ev yolu da ayni metodu kullaniyor.
  Kayittaki REGION.FLAGS artik bolgeye uygulaniyor.)

**Dogrulama sirasinda netlesen sey:** 56T evleri sahibi `MORE1`'de tutuyor ve bunu
shard'in KENDI scriptleri okuyor (`<link.uid.<link.more1>.account.name>`). Source-X
CItemMulti sahibi yalnizca OWNER'dan okur, MORE1'i hic kullanmaz. Bu yuzden MORE1'i
sahip saymak motora Source-X'te olmayan bir kural eklemek olurdu - yapilmadi. Eksik
olan sahiplik degil, BOLGEYDI; o kapandi.

**Kapanis:** tam suite **3.271 basarili / 0 basarisiz** (+2).

**Acik kalan:** sunucu calisirken silinen sahipsiz multi bolgesini sizdirir (ev kaydi
olan yapida silme yolu zaten var). Ayrica bu yapilarin kilitleme/guvenli kap/decay
yonetimi shard scriptlerinde; motorun House kaydina baglanmalari ayri bir karar.

### IS-2b - yapinin kendi tanimina cozulmesi (7 Eylul 2026)

Kaynak: IS-2 sirasinda ortaya cikti (klasik gemi HATCH/PLANK dilimi).

- [x] **B2-1 (P1)** - Yapinin kaydi `[MULTIDEF]` adiyla baslar ve ne ID ne TYPE yazar;
  yukleyici yalnizca ITEMDEF soruyordu, tekne grafik 0 / tur Normal yukleniyordu.
  (YAPILDI: `WorldLoader.ResolveMultiDef` - baslik multi tanimina cozuluyor, kimlik ve
  tur oradan geliyor. Referansta ayrim yok: CItemBaseMulti.)
- [x] **B2-2 (P1)** - Yapinin parcalari esya kimligiyle baslayan sayisal basliklar
  kullanir ve ID satiri yazmaz; onlar da grafik 0 / tur Normal geliyordu, yani tekne
  kendi iskelesini taniyamiyordu. (YAPILDI: sayisal baslik Sphere sayisi olarak
  okunuyor.)
- [x] **B2-3 (P2)** - `ShipEngine.DeserializeFromWorld` OWNER olmadan tekneyi
  kurmuyordu; 56T'nin sekiz teknesinin hicbirinde OWNER yok. (YAPILDI: sahip, gemiyi
  gemi yapan sey degil - tur yapar; sahipsiz gemi `Serial.Invalid` sahiple kaydoluyor.)

**Gercek veri sonucu:** 7 tekne ambari ve iskeleleriyle kaydoldu. Sekizinci kaydinda
kendi `TYPE=t_multi` satirini tasiyor - ornegin turu tanimindan ustun, dogru davranis.

**Kapanis:** tam suite **3.269 basarili / 0 basarisiz** (+3).

**Acik kalan:** klasik EV kaydi da ayni sekilde `HOUSE.OWNER` tag'ini bilmiyor
(`CreateHouseFromTags` onu ariyor); 0.56 evleri sahibi MORE1'de tasiyor gorunuyor -
dogrulanmadi. Yani evler hala kaydolmuyor. Ayri is olarak durur.

### PLAN-106 kapanisi - eski shard'in oldurme sayaclari (7 Eylul 2026)

- [x] **P106-11 (P1)** - 0.56'nin KILLSPLAYER/KILLSNPC anahtarlari SAVE.* tag'ine park
  ediliyordu; 56T'de 834 karakter cinayet sayisi olmadan, yani masum yukleniyordu.
  (YAPILDI: referansin yapisi tek sayactir - KILLS, cinayet sayisi; KILLSPLAYER ona
  cevriliyor. KILLSNPC'nin referansta karsiligi YOK, ikinci sayac uydurulmadi; script
  okuyabilsin diye tag olarak korunuyor. Ceviri, referansin kendi legacy import
  yolundaki desenle ayni yerde - yukleyicide.)

**Kullanici karari:** "Source-X yapisi nasilsa oyle yapalim." Referansta tek sayac
oldugu dogrulandi (CCharPlayer.h:49, CCharPlayer.cpp:553, CWorldImport.cpp:750); iki
adin da Source-X kaynak agacinda hic gecmedigi ayrica arandi.

**Olcum:** eslenmeyen anahtar turu **2 -> 0**. 56T dokumu artik hicbir anahtari
park etmeden yukleniyor.

**Kapanis:** tam suite **3.266 basarili / 0 basarisiz** (+1).

### PLAN-106 besinci dilim - klasik geminin ambari ve iskeleleri (7 Eylul 2026)

- [x] **P106-9 (P2)** - Klasik gemi kaydindaki HATCH ve PLANK satirlari reddedilip
  SAVE.* tag'ine park ediliyordu; kayit hicbir bileseni listelemedigi icin bu iki uid o
  parcalarin TEK kaydiydi. (YAPILDI: `SHIP.HOLD` / `SHIP.PLANKS` olarak okunuyor,
  `ShipEngine.DeserializeFromWorld` bunlari bilesen olarak kaydediyor.)
- [x] **P106-10 (P2)** - Klasik kaydin TEMIZLENMIS uid isaretcisi (indeks bitleri dolu,
  ornegin 04FFFFFFF) gecerli sayiliyordu; 56T'nin sekiz gemisinden yedisi ambar alaninda
  bunu tasiyor. (YAPILDI: `Serial.NamesAnObject` - referansin CUID::IsValidUID kurali.
  Mevcut `IsValid` degistirilmedi; motorun kendi alanlari icin dar soru orada kaliyor.)

**Olcum:** eslenmeyen anahtar turu **4 -> 2**. Kalan ikisi KILLSPLAYER/KILLSNPC.

**Kapanis:** tam suite **3.265 basarili / 0 basarisiz** (+8).

**Bu dilimde ORTAYA CIKAN yeni eksik (ayri is):** multi'nin TYPE'i `[MULTIDEF]`
blogundan gelir ve bu motor onu esyada degil multi kayit defterinde tutar. Sonuc: import
edilen tekne `ItemType.Ship` degil; bu yuzden `ShipEngine.DeserializeFromWorld`in tur
kapisindan gecmiyor. Ayrica ayni yerde OWNER de araniyor, 56T'nin sekiz gemisinin
hicbirinde OWNER yok. Yani gemiler su an hic kayit olmuyor; bu dilim verinin
KAYBOLMAMASINI sagliyor, gemiyi canlandirmiyor.

### PLAN-106 dorduncu dilim - klasik tasin loncasi (7 Eylul 2026)

- [x] **P106-7 (P1)** - Klasik lonca/sehir tasinin ALIGN, ABBREV, CHARTER<n>, WEBPAGE
  ve MEMBER satirlari reddedilip SAVE.* tag'ine park ediliyordu; lonca katmani hicbir
  lonca gormuyor, sonraki kayit da hicbirini geri yazmiyordu. 56T'de 10 tas / 144
  satir. (YAPILDI: `GuildManager.TryApplyClassicStoneKey` klasik satiri motorun kendi
  GUILD.* bicimine ceviriyor; gercek veride 10 lonca geri geldi, en buyugu 13 uyeyle.)
- [x] **P106-8 (P2)** - Yeniden kurma yalnizca GUILD.NAME tag'ine bakiyordu; klasik tas
  kendi adini yazmaz. (YAPILDI: liste tasiyan tas kendi adini aliyor -
  CItemStone::GetName.)

**Sinir - dogrulanmamis dal:** listedeki 100/101 yetkileri (ENEMY/ALLY) uye degil savas
/ittifak kaydidir; iliski olarak ceviriliyor. 56T dokumunde ornegi YOK, dal referansin
bicim yorumuna ve birim testine dayaniyor.

**Tur ici duzeltilen kendi hatali testim:** savas testini var olmayan bir tasa karsi
kurmustum; mevcut kod, yuklenmemis loncaya karsi iliskiyi bilerek dusuruyor. Test iki
gercek tasla yeniden kuruldu.

**Olcum:** eslenmeyen anahtar turu **8 -> 4**.

**Kapanis:** tam suite **3.257 basarili / 0 basarisiz** (+4).

### PLAN-106 ucuncu dilim - yapinin bolgesi (7 Eylul 2026)

- [x] **P106-6 (P2)** - Multi kaydindaki `REGION.TAG.<ad>` satirlari reddedilip SAVE.*
  tag'ine park ediliyordu (56T'de 93 ev sahibi + 1 hp_bar); sonraki kayitta kayboluyordu.
  (YAPILDI: satir multi uzerinde tutuluyor, kendi adiyla geri yaziliyor ve
  `HousingEngine`/`ShipEngine` bolgeyi gerceklestirirken bolgeye kopyalaniyor.)

**Bilincli sinir - okuma yakalanmadi:** `REGION.<anahtar>` okumasi zaten `ObjBase`
uzerinden nesnenin icinde durdugu bolgeye gidiyor. Ilk denemede esya uzerinde
yakalamistim; bu, orada duran her sey icin bolgeyi gizledi ve test yakaladi. Depolama
(tag) ile okuma (bolge) ayri tutuldu.

**Olcum:** eslenmeyen anahtar turu **10 -> 8**.

**Kapanis:** tam suite **3.253 basarili / 0 basarisiz** (+3).

**Acik kalan:** bolge tetikleyicilerinin nesne kimligi - `FireRegionEvents` script'i
KARAKTER uzerinde calistiriyor, referansta ise bolge nesnesi uzerinde calisir ve SRC
karakterdir. Yani bolge @Enter icinde `<TAG.owner>` su an karakterin tag'ini okur.
Ayri bir tur ister; bu turda dogrulanmis bir hata olarak sayilmadi, statik gozlemdir.

### PLAN-106 ikinci dilim - kaydin turu tanimindan gelir (7 Eylul 2026)

- [x] **P106-4 (P1)** - Harita pinleri (59) ve kitap sayfalari (18) klasik kayittan
  hic okunmuyordu: kitap/harita/gemi property dallari HAM `_type` alanina bakiyor,
  klasik kayitta ise TYPE satiri olmadigi icin o alan okuma sirasinda Normal kaliyor.
  (YAPILDI: `Item.EffectiveType` - ornegin kendi turu varsa o, yoksa tanimin turu;
  get ve set yuzeyinin yedi kapisi buna bagli. Duzeltme gecici geri alinip testin eski
  davranisi yakaladigi dogrulandi.)
- [x] **P106-5 (P2)** - Paketli gercek veri gecisi yukleyici cozumleyicilerini
  kurmuyordu; sunucunun okudugu anahtarlari motorun eksigi gibi gosteriyordu.
  (YAPILDI: gecis artik Program.cs:806 kablolamasini aynaliyor.)

**Olcum:** paket + sunucu kablolamasiyla eslenmeyen anahtar turu **17 -> 10**.

**Kapanis:** tam suite **3.250 basarili / 0 basarisiz** (+3).

**Acik kalan (IS-2'nin devami):** KILLSPLAYER 897, KILLSNPC 286, REGION.TAG.owner 93,
ALIGN/MEMBER 20, HATCH 8, ABBREV 7, CHARTER0 1, PLANK 1, REGION.TAG.hp_bar 1. Ayrica
`Item` icindeki kalan ham `_type` kapilari (yigin, hafiza, multi-custom, statik blok)
bu turda BILINCLI olarak degistirilmedi; davranis yollari, ayri bir tur ister.

### PLAN-106 ilk dilim - shard'in kendi skill adlari (7 Eylul 2026)

Kaynak: port plani [IS-2](D:/Projeler/Yunus/sphereNet/docs/PORT_PLAN_ILERLEME_TR.md),
56T gercek veri dokumundeki eslenmeyen kayit anahtarlari.

- [x] **P106-1 (P1)** - Paketin yeniden adlandirdigi skill'ler kayittan hic
  okunmuyordu: 56T'de skill 54 Sailormanship, skill 19 Farming; 128 karakterin degeri
  SAVE.* tag'ine park edilip skill sifir doniyordu. (YAPILDI: skill adlari yuklenen
  tanimlardan da cozuluyor - `SkillNames.TryResolve`.)
- [x] **P106-2 (P1)** - Paket adi ortak defname tablosundan araniyordu; 56T'deki
  "Farming" adli NEWBIE kaynagi aramayi kazanip skill'i gizliyordu. (YAPILDI: skill
  bloklari kendi adlariyla indeksleniyor - `DefinitionLoader.TryGetSkillIndexByName`;
  ortak tablo yedek.)
- [x] **P106-3 (P2)** - Ayni soruyu uc ayri tablo yanitliyordu (script, dunya
  yukleyicisi, yerlesik enum). (YAPILDI: tek tablo `SkillNames`; yukleyicinin kopyasi
  klasik yazimlarin yarisini da bilmiyordu.)

**Olcum duzeltmesi:** eslenmeyen anahtar envanteri paket YUKLENMEDEN olculuyordu;
anlami paketten gelen anahtar orada haksiz yere "motor eksigi" gorunuyor. Paket
yukluyken calisan ikinci gecis eklendi
(`Sphere56TSaveCompatTests.WithTheScriptPackLoaded_...`). Paketli olcumde 17 tur 15'e
dustu.

**Kapanis:** tam suite **3.247 basarili / 0 basarisiz** (+6). Yeni testler:
`LegacySaveKeyParityTests` (5) ve paketli gercek veri gecisi (1).

**Acik kalan (IS-2'nin devami):** paketli olcumde kalan 15 anahtar turu - KILLSPLAYER
897, KILLSNPC 286, REGION.TAG.owner 93, PIN 59, ALIGN/MEMBER 10, HATCH 8, ABBREV 7,
BODY.0-3 18, CHARTER0 1, PLANK 1, REGION.TAG.hp_bar 1. Siniflandirma port plani
dosyasinda.

### SX-01B — Envanter ilk tarama (6 Eylül 2026)

SphereNet `7a11130da128af76417574a8003d7915ee6d737f`, Source-X `92ced0ba`.
[Ayrıntılı kanıt raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_01B_ENVANTER.md).
Altı yeni bulgu izole GameClient handler senaryolarıyla çalıştırıldı; Source-X
tarafı kaynak karşılaştırmasıdır. Üretim kodu değiştirilmedi. Tam test sonucu
2348/2348 başarılı. 01A'nın beş eski çalışma senaryosu da düzeltmeleri doğruladı.

**Yeniden kontrol:** Yukarıdaki tamamlanmış SX-01B-01–06 kayıtları geçerlidir.
`6804d29` üzerinde altı eski deney beklenen sonucu verdi; bu bölümdeki yinelenen
açık kutular kaldırıldı. Yeni kap kontrolünün trade bütünleşme eksiği SX-02-02'dir.

01B-04/05 eski veya özel hazırlanmış istek/UID bilgisi gerektiren erişim sınırı
senaryolarıdır; normal istemcinin bunları kendiliğinden ürettiği iddia edilmiyor.
Ek envanter varyantları rapor sonunda ayrıldı; sıradaki ana bölüm 02 güvenli ticaret.

### SX-02 — Güvenli ticaret ilk tur (6 Eylül 2026)

SphereNet `6804d29`, Source-X `92ced0ba`.
[Kanıt ve tekrar raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_02_GUVENLI_TICARET.md).
Tam suite 2362/2362 başarılı. Dört bulgu izole handler/world deneyleriyle üretildi.

- [x] **SX-02-01 (P1)** — `HandleSecureTrade` param değerini kullanmalı;
  `param=0` mevcut toggle yaklaşımında onayı açıp transferi tamamlayabiliyor.
  (YAPILDI, `f42ea6a`: `trade.SetAccept(_character, param != 0)` — paketin kendi
  bayrağı okunuyor; ayrıca gönderen KENDİ kabını adlandırmak zorunda
  (receive.cpp:1132), yoksa bayrağı okumak partnerin onayını set etmeye yarardı.)
- [x] **SX-02-02 (P2)** — Trade container sahipliği/açılma/erişim bağlamını
  bütünleştir; normal oyuncu kendi teklif nesnesini pickup ile geri alamıyor.
  (YAPILDI, `f42ea6a`: pencere sahibine `Layer.Special`de `EqTradeWindow` olarak
  kuşandırılıyor (CClientUse.cpp:1414/1420); `CanReachInsideContainer` top-level
  karakter kendisi olduğu için teklifi geri alabiliyor, partnerin penceresi
  erişim dışı kalıyor.)
- [x] **SX-02-03 (P1)** — Başlatılamayan trade'in ilk eşyasını iade et;
  REFUSETRADES reddinde eşya çanta ve DRAGGING dışında karaktere bağlı kalıyor.
  (YAPILDI, `f42ea6a`: `InitiateTrade` artık `bool` dönüyor ve çağıran eşyayı
  kaldırma noktasına geri sektiriyor (CClientEvent.cpp:325); ölü/uzak/çevrimdışı,
  REFUSETRADES ve zaten-ticarette dallarının tamamı bu yoldan geçiyor.)
- [x] **SX-02-04 (P1)** — Trade içeriğinden nesne çıkarılınca iki onayı sıfırla;
  World.RemoveItem sonrası partner onayı korunup farklı teklif tamamlanıyor.
  (YAPILDI, `f42ea6a`: `Item.OnTradeWindowChanged` kabın kendi değişiminden
  sürülüyor — ekleme ve çıkarma aynı invariant üzerinden onayı düşürüyor
  (CItemContainer.cpp:557/:798), handler nezaketine bağlı değil.)

Normal tamamlama, disconnect iadesi ve ölüm öncesi trade iptal köprüsü kontrolleri
geçti. Sonraki 02B turu: save/load, script sözleşmesi, uzaklaşma/harita ve diğer
durum değişimleri. Tam kapsam henüz kapanmadı; kapasite ön reddi raporda tasarım
farkı olarak ayrıldı, yeni hata hükmü verilmedi.

### SX-02B — Yeni kayıt/script bulguları (6 Eylül 2026)

Kullanıcı SX-02-01–04 sorunlarının sürdüğünü bildirdi; o turda açık durumları
korundu. Bu tur onları düzeltmeden üç ayrı senaryo çalıştırıldı. SphereNet `db97de6`.
(Dördü de sonradan `f42ea6a` ile kapandı; yukarıdaki kayıtlara bakınız.)
[02B raporu](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_02B_KAYIT_SCRIPT.md).

- [x] **SX-02B-01 (P1)** — Aktif trade snapshot'ından yüklemede teklif eşyasını
  özgün sahibine geri bağla; nesne oturumsuz ve sahipsiz eski trade kabında kalıyor.
  (YAPILDI, `f42ea6a`: `Program.WorldBootstrap.RecoverInterruptedTrades` her
  pencereyi sahibine boşaltıp kaldırıyor; sahibi bulunamayan eski kayıtta eşya
  silinmek yerine pencerenin durduğu yere bırakılıyor (CItem.cpp:1005).)
- [x] **SX-02B-02 (P2)** — Partnerin aktif bağlantısını doğrula; IsPlayer=true
  fakat GameClient olmayan karakterle trade açılabiliyor.
  (YAPILDI, `f42ea6a`: `InitiateTrade` içinde `!partner.IsOnline` reddi —
  Cmd_SecureTrade çevrimdışı oyuncuyu da reddeder (CClientUse.cpp:1338).)
- [x] **SX-02B-03 (P2)** — @TradeAccepted veto ile gerçek iptali ayır; Source-X
  pencereyi açık bırakırken SphereNet kapatıp iki @TradeClose çalıştırıyor.
  (YAPILDI, `f42ea6a`: RETURN 1 yalnız O DEĞİŞİMİ veto ediyor ve `return` ile
  çıkıyor — pencere açık, iki teklif ve iki onay işareti yerinde
  (CItemContainer.cpp:189/:145); @TradeClose artık ateşlenmiyor.)

Üçü izole deneyle üretildi; Source-X karşılıkları kaynak okumadır. Üretim kodu
değiştirilmedi. Önceki 2362 test sonucu geçerli kaynak ağacına aittir; tam suite
bu tur tekrar çalıştırılmadı. Sonraki ana alan 03 dövüş; kalan ticaret varyantları
02B raporunda açık kapsam olarak korunuyor.

### SX-03A — Dövüş ilk tarama (6 Eylül 2026)

SphereNet `f42ea6a`, Source-X `92ced0ba`.
[Ayrıntılı rapor](D:/Projeler/Yunus/sphereNet/docs/reviews/SOURCE_X_BOLUM_03A_DOVUS.md).
Üç fark izole deneyle doğrulandı; tam suite 2374/2374 başarılı. Üretim koduna
o incelemede dokunulmadı; üçü de sonradan `f98612e` ile kapandı.

- [x] **SX-03A-01 (P2)** — Normal mühimmat aramasında kilitli alt kaplara inme;
  FindAmmoInContainerCore IsSearchableContainer kontrolünü kullanmıyor.
  (YAPILDI, `f98612e`: iniş `item.IsSearchableContainer` ile kapatıldı —
  ContentFind de inmeden önce IsSearchable bakar (CContainer.cpp:236).)
- [x] **SX-03A-02 (P1)** — REFLECTPHYSICALDAM hasarını bağışıklık sözleşmesinden
  geçir; Invul saldırgan doğrudan HP yazımı nedeniyle 20 can kaybediyor.
  (YAPILDI, `f98612e`: Blood Oath, Reactive Armor ve REFLECTPHYSICALDAM tek
  `ApplyReflectedDamage` girişini paylaşıyor; referans yansımayı olağan
  OnTakeDamage'den geçirir ve özyineleme DAMAGE_REACTIVE ile durur
  (CCharFight.cpp:1021/:1015/:642). SpellEngine yansıması ve zehir tikleri de
  aynı girişe alındı.)
- [x] **SX-03A-03 (P2)** — Vuruş sonrası proc'ları ana HP hasarından sonra uygula;
  HITFIREBALL callback'i 20 hasarlık vuruşta hedefi hâlâ 100 HP'de görüyor.
  (YAPILDI, `f98612e`: silahın kendi hasarı önce (CCharFight.cpp:2259), proc'lar
  sonra (:2270). Asıl bedel proc'un gördüğü HP değil: proc hedefi öldürünce
  vuruşun hasarı hiç inmiyor ve kayda geçmiyordu — öldürme, murder sayacı ve
  yağma hakkı proc'a gidiyordu.)

Sonraki 03B: windup/hedef değişimi, silah değiştirme, player/NPC zamanlaması,
miss/parry/veto cephane yolları. İlk tarama bütün dövüş kategorisini kapatmaz.

---

## İŞ-18 — Vendor ve secure trade uçtan uca (PLAN-401, 10 Eylül 2026)

Port planının Dalga 4'e ilk girişi. PLAN-401'in sekiz ayağı tek tek ölçüldü;
altısı zaten referansla aynı çıktı, ikisi (yeniden yükleme + trigger sözleşmesi)
üç gerçek boşluk verdi.

### Ölçülüp DEĞİŞTİRİLMEYEN ayaklar (kanıtıyla)

| Ayak | Durum | Kanıt |
|---|---|---|
| Fiyat / miktar | Doğru | `ProcessBuy` her satırın fiyatını O SATIRIN kendi PRICE tag'inden alır (aynı BaseId'li iki farklı fiyatlı stok satırı ayrışır), tekrar eden satır ve stok aşımı reddedilir |
| Dolu çanta | Doğru | `CanAcceptTradeItems` slot (`MaxContainerItems`) + ağırlık; `DeliverToBuyer` taşan malı ayağa bırakır, hiç yok etmez (ItemBounce) |
| İç içe altın | Doğru | `CountGold`/`RemoveGold` `EnumerateContainerContentsRecursive` üzerinden alt çantalara iner |
| Teklif değişimi | Doğru | `Item.NotifyTradeContentChanged` → `OnTradeWindowChanged`; ekleme VE çıkarma iki onay işaretini de düşürüyor (CItemContainer.cpp:557/:798) — kap değişmezi, handler nezaketi değil |
| Disconnect | Doğru | `GameClient.cs:288` → `AbortActiveTradeOnDisconnect` (CChar::ClientDetach, CChar.cpp:490) |
| Ölüm | Doğru | `Program.EngineWiring.cs:1812` → `CancelActiveTradeOnDeath` (CCharAct.cpp:4338) |
| Takas penceresi ağırlığı | Doğru (bilinçli) | Pencere `_equipment` dizisinde değil ve ağırlık döngüsü VendorStock'tan sonraki katmanları atlıyor — referans da pencere ağırlığını sahibine yazmaz (CContainer.cpp:150/198) |

### Kapanan boşluklar

- [x] **İŞ-18-01 (P1) — Eşyanın kendi TYPE'ı kaydı aşmıyordu.**
  Referans TYPE satırını `!pItemDef->IsType(m_type)` koşuluyla yazar
  (CItem::r_Write, CItem.cpp:2461). `WorldSaver` yalnız Multi / MultiCustom / Ship
  için yazıyordu; bunlar zaten ITEMDEF'i olmayan yapı türleriydi. Sonuç: scriptin
  `TYPE=t_door`'u ya da motorun kurduğu her tür restart'ta ITEMDEF'e dönüyordu
  (`MaterializeDefinitionType` yalnız Normal olanı doldurur).
  **Düzeltme:** `Item.DefinitionType` / `Item.HasInstanceType` + saver'da genel kural.
  Ham `_type` bilinçli: `ItemType` getter'ı tiledata'dan kapı tahmini de yapar, o
  kaydın iki yakasında da türetilebilir olduğu için nesne durumu değildir.
  **Ölçüm (56T, gerçek shard):** 76.359 eşyanın 2.383'ü kendi türünü taşıyor; kaynak
  dökümdeki TYPE satırı sayısıyla karşılaştırılıyor (test), yani kayıt büyümüyor.

- [x] **İŞ-18-02 (P1) — Takas ortasında alınan kayıt malı yiyordu.**
  Takas penceresi yalnız CANLI İSTEMCİSİ olan bir oyuncuda var olabilir; referans
  yüklemede pencereyi dağıtır — içindekiler sahibinin çantasına, pencere silinir
  (FixWeirdness sonuç kodu 0x2220, CItem.cpp:1005). Bizde kayda giriyordu ve
  Special katmanında geri geliyordu: o katman ekipman dizisinde değil (hafıza
  eşyalarıyla paylaşımlı), hiçbir envanter görünümü ulaşmıyor, hiçbir takas oturumu
  sahiplenmiyor — mal dünyada var, oyuncu için yok.
  **Düzeltme:** `WorldLoader.DissolveStaleTradeWindows`, `RebuildContainerIndex`
  sonrasında. Sahipsiz pencere de içindekini bırakır (yere), yanında götürmez.

- [x] **İŞ-18-03 (P2) — @TradeAccepted malı adlandırmıyordu.**
  Referans, alan tarafın alacağı eşyaları trigger argümanlarının nesne listesine
  koyar (`m_VarObjs.Insert(i, pItem)`, CItemContainer.cpp:196) ve ARGN1 bunların
  SAYISIDIR; script listeye REF1..REFn olarak erişir (CScriptTriggerArgs.cpp:189).
  Bizde yalnız sayı geçiyordu, yani trigger neyin el değiştirdiğini söyleyemiyordu.
  **Paket tarafından ölçüm:** `Scripts-X-main/housing/house_typedefs.scp:516` tam bu
  sözleşmeyi kullanıyor (`for <ARGN1>` + `<REF<dLOCAL._FOR>.TYPE>==t_trade_house_deed`)
  — REF'siz döngü her turda 0 okur ve takas edilen ev hiç el değiştirmez.
  **Düzeltme:** `TriggerArgs.Refs` (Game) → `SharedRefs` (Scripting) →
  `ScriptScope.RefMap`. Harita PAYLAŞILIYOR (LOCAL havuzu gibi), yani zincirdeki her
  blok aynı REF'leri görür ve scriptin kendi `REFn=` yazımı fire sonrası okunabilir.
  ARGN1/ARGN2 de refleri üreten aynı yürüyüşten sayılıyor.

**Testler:** `TradeReloadParityTests` (5), `TradeAcceptedRefsTests` (4),
`Sphere56TSaveCompatTests` içine TYPE gidiş-dönüş ölçümü. Üç düzeltme tek tek geri
alınıp ilgili testlerin eski davranışı yakaladığı doğrulandı (5→3, 5→2, 4→2).
Tam suite 3.373 / 0.

**Kayıtlı kalan:** referans TYPE'ı ADIYLA yazar (`t_door`), biz sayısal yazıyoruz.
İkisi de aynı okuyucudan geçiyor (`ParseItemType` ad + sayı kabul eder), ama bizim
kaydımızı okuyan üçüncü taraf araç için ad daha okunaklı olurdu. Kapsam dışı bırakıldı.

---

## İŞ-19 — Dövüşte hedef değişimi ve tehdit (PLAN-402 birinci dilim, 10 Eylül 2026)

PLAN-402'nin sekiz ayağından **"hedef değişimi"**. Ölçüm iki taraftan: canlı
paketin `ATTACKER.*` kullanımı (paket tarafı) ve referansın `Fight_Attack` →
`Attacker_Add` → `NPC_FightFindBestTarget` zinciri (motor tarafı).

### Paket tarafı ölçüm

`oldSphere/` + canlı paket taranınca `ATTACKER.` ile başlayan 12 farklı anahtar
kullanımı çıktı. Sözdizimi/dokümantasyon dosyaları elendikten sonra **canlı
pakette gerçekten kullanılan ve bizde çözülmeyen dört tanesi** kaldı:

| Anahtar | Yer | Bizdeki durum (önce) |
|---|---|---|
| `<ATTACKER.0>` | `dialogs/sphere_dialogs_prop.scp:530` | çözülmüyor (alt anahtar zorunluydu) |
| `<ATTACKER.LAST.DAM>` | aynı dosya `:537` | çözülmüyor (LAST yalnız uid) |
| `<ATTACKER.MAX.DAM>` | aynı dosya `:541` | çözülmüyor (MAX yalnız uid) |
| `ATTACKER.CLEAR` | `functions/sphere_functions.scp:246` | yazma tarafı yalnız `n.IGNORE` kabul ediyordu |

Yani shard'ın kendi oyuncu-bilgi dialogu boş basıyordu ve listeyi temizleyen
fonksiyonu hiçbir şey yapmıyordu.

### Kapanan boşluklar

- [x] **İŞ-19-01 (P2) — `ATTACKER.*` seçici modeli.**
  Referans önce bir SEÇİCİYİ satıra çözer (sayı / `MAX` = en çok hasar / `LAST` =
  en son darbe), sonra o satırda alan okur ya da yazar; alan yoksa satır kendi
  UID'siyle cevap verir (CChar.cpp:2414 / :3733). Okuma: `ID <uid>`, `TARGET`,
  `MAX`, `LAST`, sayı × `DAM|ELAPSED|THREAT|IGNORE|UID|<boş>`. Yazma: `CLEAR`,
  `DELETE <uid>`, `ADD <uid>`, `TARGET <uid>`, `n.{DAM|ELAPSED|THREAT|IGNORE|DELETE}`.

- [x] **İŞ-19-02 (P2) — Liste sırası kararlı.**
  Darbe alan satır `ATTACKER.LAST` onu göstersin diye sona taşınıyordu; bu,
  altındaki her indisi kaydırıyordu. `ATTACKER.n` scriptin iki satır arasında
  tuttuğu tutamak olduğu için bu tek başına bir hata sınıfı. Referans yalnızca
  sona ekler (`Attacker_Add`); LAST son-darbe damgasından çözülüyor.

- [x] **İŞ-19-03 (P1) — Liste iki yönlü.**
  Referansta `Fight_Attack` saldırdığın hedefi KENDİ listene yazar
  (CCharFight.cpp:1442). Bizde listeyi yalnız alınan hasar dolduruyordu, yani
  hedef ölünce sıradaki rakip listede yoktu: motor `NPC_FightFindBestTarget`
  yerine tüm görüş menzilini yeniden tarıyordu. Üç saldırgandan birini yeni
  öldürmüş bir yaratığın diğer ikisinin ortasında boş durmasının kökü bu.
  `NPC_FightFindBestTarget` portlandı: yoksayma satırı + `@HitIgnore` kaçış
  kapısı, menzilli silahta `ArcheryMin/MaxDist`, LOS, sonra tehdit-ya-da-mesafe.

- [x] **İŞ-19-04 (P1) — THREAT uydurmaydı.**
  Yerinde `Math.Clamp(TotalDamage / 2, 0, 60)` vardı ve motivasyon puanına
  karıştırılıyordu — referansta böyle bir formül YOK. Gerçek model: satırda
  saklanan tamsayı (`ATTACKER.n.THREAT`), oyuncuda hiç tutulmaz
  (CCharAttacker.cpp:205), `NPC_AI_THREAT` açıkken hedef seçimini TEK BAŞINA
  belirler, sahibin emri `ATTACKER_THREAT_TOLDBYMASTER`(=1000) + mevcut en yüksek
  taşır ve emredilen hedef motivasyonu düz 100'e sabitler (CCharNPCStatus.cpp:912).
  Kayda yazılıyor (ATTACKER satırına dördüncü alan; eski kayıt yine okunuyor).

- [x] **İŞ-19-05 (P2) — @Attack argümansızdı.**
  Referans ARGN1=tehdit, ARGN2=yoksay verir ve ikisini de GERİ OKUR
  (CCharFight.cpp:1433/:1437); `@CombatAdd` de aynı (CCharAttacker.cpp:38/:45) ve
  RETURN 1 eklemeyi iptal eder. Bizde @Attack hiçbir argüman taşımıyordu: script
  kavgayı reddedebiliyor ama ağırlıklandıramıyordu.

- [x] **İŞ-19-06 (P1) — Öldürme kredisi hasar istemiyordu.**
  `EnumerateOffenders` her satırı kredilendiriyordu; referans yalnızca
  `amountDone > 0` olanı (CCharAct.cpp:4361). Liste artık yalnız vuranları
  tutmadığına göre bu kapı taşıyıcı: bir kez savurup ıskalamak pay kazandırırdı.

### Bilinçli olarak REFERANSTAKİ GİBİ bırakılanlar

- `@Attack`'ta yazılan yoksayma bayrağı, hedef HENÜZ listede değilse hiçbir yere
  düşmez: referans `Attacker_SetIgnore`'u eklemeden ÖNCE çağırır ve taze satır
  yoksayılmamış okunur (CCharFight.cpp:1440 → :1446). Testte kilitli.
- Tekrar edilen emir satırı yeniden tohumlamaz (`Attacker_Add` var olan satırda
  erken döner, CCharAttacker.cpp:21).

### Kayıtlı sapmalar

- `@CombatStart` bizde hedef değişiminde istemci tarafında ateşleniyor; referans
  onu liste BOŞKEN `Attacker_Add` içinde ateşler. Taşımak geniş bir davranış
  değişikliği; bu turda dokunulmadı.
- Hasarı sıfır olan "katılımcı" satırı kaydedilmiyor (`RestoreAttacker` hasar
  şartı arıyor). Referans zaten listeyi ölümde temizler; geçici satırın restart'ı
  aşması gerekmiyor.

**Testler:** `AttackerListParityTests` (9), `CombatEngagementParityTests` (16),
`AttackerThreatPersistenceTests` (2). Dört düzeltme tek tek geri alındı ve
sırasıyla 4 / 8 / 1 / 1 test kırmızıya döndü. Tam suite 3.400 / 0.

**PLAN-402'nin kalan ayakları:** menzil/LOS, mühimmat, swing timer'ı,
parry/yansıma (C-dalgalarında kapsandı, yeniden ölçülecek), ölüm ve trigger veto
sırası.

---

## İŞ-20 — Swing durumu ve @HitCheck sözleşmesi (PLAN-402 ikinci dilim, 10 Eylül 2026)

### Ölçülüp DEĞİŞTİRİLMEYEN ayaklar

PLAN-402'nin "menzil/LOS", "mühimmat" ve "timer" ayakları `Fight_CanHit` /
`Fight_Hit` satır satır karşılaştırıldı; C-dalgalarının kapsamı bunları zaten
kapatmış:

| Ayak | Kanıt |
|---|---|
| Min/max menzil, LOS | `CombatHelper.ValidateSwingPrep` + `EvaluateHitTime` (menzilli/yakın ayrı) |
| Yay + kalkan | `HasShieldEquipped` → Abort, `Msg.ItemuseBowShield` |
| Okçuluk hareket gecikmesi | `CombatArcheryMovementDelay`, `COMBAT_ARCHERYCANMOVE` / `STATF_ARCHERCANMOVE` |
| Gemi kuralı | `IsCombatBlockedByRegion` + `COMBAT_ALLOWHITFROMSHIP` |
| Paralize / uyku | `COMBAT_PARALYZE_CANSWING`, uyuyan hedef iki yolda da bekletiliyor |
| Ok ekonomisi | İska: yığından 1 düşer, %40 hedefin ayağına decay'li; isabet: hedef NPC ise %40 gövdesine, değilse harcanır; `LOCAL.Arrow` + `ArrowHandled`; NPC ıskada ok harcamaz (referansta `m_pPlayer` kapılı) |

### Kapanan boşluklar

- [x] **İŞ-20-01 (P2) — Tetikleyicinin sayısal RETURN'ü motora ulaşmıyordu.**
  `TriggerResult` yalnız sıfır/sıfır-değil ayırır. Referansın birçok sözleşmesi
  NEGATİF dönüş kullanır (@HitCheck -1 ve -2). Ham değer artık
  `ScriptScope.ReturnValue` → `TriggerArgs.ReturnValue` → `TriggerArgs.ReturnNumber`
  yoluyla geri geliyor; genel bir motor yeteneği, başka trigger'lar da kullanabilir.

- [x] **İŞ-20-02 (P1) — @HitCheck ters okunuyordu.**
  Bizde "her doğru dönüş = zorunlu ıska" idi. Referans (CCharFight.cpp:1770-1779):
  `RETURN 1` → **ARGN1 dönen swing durumudur**; `RETURN -1` → WAR_SWING_INVALID
  (hedefi bırak); `RETURN -2` → düzenlemeleri al, gömülü yolu yine de çalıştır;
  başka her şey → ARGN1 ve `LOCAL.Recoil_NoRange` geri okunur, akış devam eder.
  **Ölçüm (paket tarafı):** `Scripts-X-main/_incomplete/combat_override.scp` bütün
  bir script-tarafı dövüş katmanı ve tempoyu tam bu sözleşmeyle kuruyor
  (`:49` `argn1 SWING_READY` + `return 1` = *bekle*). Bizde o beklemelerin her biri
  bir vuruş ve bir ıska olarak görünürdü.
  Durum eşlemesi: INVALID → hedefi bırak; EQUIPPING → vuruş harcandı, recoil başlar;
  READY/SWINGING → bir onda saniye bekle ve yeniden bak (upstream `_SetTimeoutD(1)`).
  **NPC yolu da aynı sözleşmeye alındı** (`NpcHitCheckOutcome`): bir script, vuran
  kendisi de olsa evcili de olsa RETURN 1'in tek bir anlamını görür.

- [x] **İŞ-20-03 (P2) — `<SWING>` yoktu.**
  Referansın kendi adı (CChar.cpp:2998/4038). Canlı paket
  `dialogs/sphere_dialogs_prop.scp:183`'te `<SWING>` ve `<DEF.war_swing.<SWING>>`
  okuyor; bizde yalnız uydurma `SWINGSTATE` yanıt veriyordu. Yazma tarafı da
  eklendi: 0 her zaman, gerisi yalnız -1..2 (referans dışını reddeder).

### Kayıtlı sapmalar

- **@HitCheck'in ARGN2'si (hasar türü) geri okunmuyor** ve `DAMAGE_FIXED` hızlı
  yolu yok. Sebebi yapısal: bizim vuruş yolu hasarı TÜRSÜZ uyguluyor
  (`ResolveAttack` bir hasar türü parametresi almıyor, tür silahtan içeride
  türetiliyor). Scriptin yazdığı türü vuruşa taşımak ayrı ve daha büyük bir iş;
  İŞ-10'da kayda geçen "tek `OnTakeDamage` kapısı" birleştirmesiyle aynı yere
  bakıyor.
- **`CANSEELOSFLAG`** hâlâ yok. Yalnız `_incomplete` demo scripti kullanıyor;
  ayrıca bizim LOS'umuz pencereleri her zaman şeffaf sayıyor (kod içinde kayıtlı
  bilinçli sadeleştirme), yani `LOS_NB_WINDOWS` bizde zaten varsayılan davranış.

**Testler:** `HitCheckReturnParityTests` (9). Dört düzeltme tek tek geri alınıp
sırasıyla 3 / 2 / 2 / 1 test kırmızıya döndü. Tam suite 3.409 / 0.

**PLAN-402'nin kalanı:** ölüm ve trigger veto sırası (parry/yansıma C-dalgalarında
kapsandı; hedef değişimi İŞ-19'da kapandı).

---

## İŞ-21 — @HitParry sözleşmesi (PLAN-402 üçüncü dilim, 10 Eylül 2026)

PLAN-402'nin "parry/yansıma" ayağı. Yansıma tarafı İŞ-9 ve İŞ-10'da kapanmıştı;
savuşturma tarafı açıktı. Referans sözleşmeyi kendi kaynağında belgeliyor
(CCharFight.cpp:2095-2119).

### Kapanan boşluklar

- [x] **İŞ-21-01 (P1) — Trigger yanlış anda ateşleniyordu.**
  Bizde BAŞARILI zardan sonra, referansta zardan ÖNCE. Argümanların bir anlam
  taşıdığı tek an bu: `LOCAL.ParryChance` zarın kullandığı şans, yani script
  motorun savuşturmayacağı yerde savuşturabilir (ve tersi). Sonradan ateşlenince
  ikisi de erişilemezdi.

- [x] **İŞ-21-02 (P1) — ARGN1 ters büyüklüktü.**
  Bizde "parry'den GEÇEN hasar", referansta "darbeden DÜŞÜLEN yüzde". Bunlar ters
  büyüklükler: daha sert savuşturmak için ARGN1'i yükselten bir script darbeden
  daha AZ düşürüyordu. 100 tam blok; `>= 100` → darbe düşer, `> 0` → yüzde kadar
  azalır. Varsayılan da artık Parrying'in **EFFECT eğrisinden** (`:2091`) geliyor;
  eğri tanımlı değilse 100.

- [x] **İŞ-21-03 (P2) — Sözleşmenin geri kalanı yoktu.**
  ARGN2 (hasar türü), ARGO (savuşturan eşya), `LOCAL.ParrySkillID` (hangi yetenek
  zar atar ve gelişir), `LOCAL.ItemParryDamageChance`, `LOCAL.Damage` (ham darbe,
  yeniden yazılabilir) ve `RETURN 1` vetosu (`:2113`).

- [x] **İŞ-21-04 (P2) — Savuşturan eşya hiç yıpranmıyordu.**
  Referans her başarılı savuşturmada `pItemHit->OnTakeDamage(1, ...)` yapar
  (`:2133`), tek kapı `LOCAL.ItemParryDamageChance`. Bizde `parryItem`
  hesaplanıyor ama hiç kullanılmıyordu: kalkan sonsuza kadar bedava savuştururdu.
  Global dayanıklılık şansı burada ikinci kez atılmıyor (referansta o roll yok).

### Kayıtlı sapmalar

- ARGN2'yi script YAZABİLİR ama yazdığı tür darbeye taşınmıyor — İŞ-20'de kayda
  geçen aynı yapısal sınır (vuruş yolu hasarı türsüz uyguluyor).
- Referans `DAMAGE_GOD` darbede bütün bloğu atlar (`:2083`). Bizde atlanacak bir
  şey yok: silah vuruşu bu yoldan hiç hasar bayrağı taşımıyor, God bayraklı
  kaynaklar (script DAMAGE verb'ü, büyü motoru) savuşturma kontrolüne uğramıyor.

**Testler:** `HitParryContractTests` (8). Üç düzeltme tek tek geri alındı ve
sırasıyla 7 / 2 / 1 test kırmızıya döndü. Tam suite 3.417 / 0 (iki kez arka arkaya
koşuldu). **Fikstür notu:** ilk halinde 11 koşuda 1 kez düşen bir kırılganlık
vardı; sınıf okuduğu paylaşımlı statiklerin YALNIZ bir kısmını sabitliyordu.
Savuşturma cevabı `CombatParryingEra` + `FeatureSE`'ye, yıpranma ise
`DurabilityLossMin/Max`'a bakıyor; hepsi sabitlendi ve 5 arka arkaya koşu temiz.

**PLAN-402 KAPANDI.** Sıradaki: PLAN-403 (magic uçtan uca).

---

## İŞ-22 — Büyü: ellerin boşaltılması (PLAN-403 birinci dilim, 11 Eylül 2026)

PLAN-403'ün cast-başlangıcı ayağı. Üç boşluk da canlı shard'da **aktif**:
`C:\sphereNetServer\sphere.ini` `EQUIPPEDCAST=0` ve `MAGICFLAGS=0`.

### Kapanan boşluklar

- [x] **İŞ-22-01 (P1) — Eller boşaltılmıyor, büyü söndürülüyordu.**
  Referans `Spell_CastStart` (CCharSpell.cpp:3544) EQUIPPEDCAST kapalıyken
  `Spell_Unequip(LAYER_HAND1/2)` çağırır; bu da eşyayı çantaya **ItemBounce**
  eder ve yalnız eşya gitmiyorsa başarısız olur (`:2849`). Bizde doğrudan
  "fizzles" dönüyordu: canlı shard'da elinde silah olan bir oyuncu hiç büyü
  yapamıyor. Port edildi; kitap/wand/kalkan elde kalır, taşınamayan eşya
  (`CanMoveItem`) ya da konacak yeri olmayan eşya yine cast'i durdurur.

- [x] **İŞ-22-02 (P2) — `CAN_I_EQUIPONCAST` okunmuyordu.**
  Bayrak `CanFlags.I_EquipOnCast = 0x8000000` olarak enum'da zaten vardı ve
  referansla birebir (CBase.h:61), ama hiçbir yer ona bakmıyordu. Paketin
  "bu eşya cast'te elde kalır" deme yolu bu.

- [x] **İŞ-22-03 (P2) — `MAGICF_CASTPARALYZED` ölü ayardı.**
  `CastStart` donmuş karakteri bayrak hiç okunmadan reddediyordu. Referansta
  orada toptan bir red YOK: donmuşluk yalnız ellerin boşaltıldığı yerde tartılır
  ve kararı `NOCASTFROZENHANDS` (`:2833`) ile `CASTPARALYZED` (`:2839/:2841`)
  verir.

### Test altyapısı — kayda değer

İŞ-21'in fikstür kırılganlığının gerçek kökü burada bulundu: **`ResetEngineStatics`
her testten ÖNCE çalışıyor**, yani test sınıfının KURUCUSUNDA yapılan sabitleme
siliniyor. Ayrıca Reset() dövüş/büyü anahtarlarının bir kısmını hiç
sıfırlamıyordu: `WeaponDefLookup`, `DurabilityEnabled/LossChance/LossMin/LossMax`,
`CombatFlags`, `CombatDamageEra`, `CombatHitChanceEra`, `ReagentsRequiredEnabled`,
`EquippedCastEnabled`. Hepsi Reset'e eklendi; iki yeni sınıf da sabitlemeyi
kurucudan test gövdesine taşıdı. (CLAUDE.md kuralı: yeni statik hook → Reset'e.)

### Kayıtlı sapma

- `SPELLCHANNELING` eşya property'si hâlâ yok. Referans onu `Spell_Unequip`'te
  `CAN_I_EQUIPONCAST`'in yanında okur, ama ne canlı pakette ne `oldSphere/`
  paketlerinde tek bir kullanımı var — tüketicisi olmayan ayar eklenmedi.

**Testler:** `SpellUnequipParityTests` (11). Üç düzeltme tek tek geri alındı ve
sırasıyla 2 / 1 / 1 test kırmızıya döndü. Tam suite 3.428 / 0, üç arka arkaya koşu.

**PLAN-403'ün kalanı:** reagent/scroll/wand mana maliyeti, hareket ve hasarla
iptal, alan etkisi, summon, seyahat.

---

## İŞ-23 — Büyü: maliyet ve yürürken cast (PLAN-403 ikinci dilim, 11 Eylül 2026)

### Ölçülüp DEĞİŞTİRİLMEYEN ayaklar

| Ayak | Kanıt |
|---|---|
| Wand mana bedava, scroll yarım | Referansta gerçek ve formülün İÇİNDE (`Calc_SpellManaCost`, CResourceCalc.cpp:536/550) — `Spell_CanCast`'teki satırlar yoruma alınmış, bizimki doğru yerde |
| Reagent yalnız kendi gücünden cast | `pObj == pCharCaster` + `!m_pNPC` kapıları bizde de var |
| Hasarla iptal | `[SPELL] INTERRUPT` eğrisi + `NPCCANFIZZLEONHIT` |
| Wand şarjı / scroll tüketimi | Var; `Spell_CanCast`'teki `m_spellcharges--` / `ConsumeAmount` karşılığı |

### Kapanan boşluklar

- [x] **İŞ-23-01 (P1) — Yürümek büyüyü iptal ediyordu.**
  Referansta "hareket büyüyü böler" diye bir kural **yok**. `OnFreezeCheck`
  (CCharAct.cpp:4539) iki cevaptan birini verir: `MAGICF_FREEZEONCAST` (ya da
  global bayrak kapalıyken `SPELLFLAG_FREEZEONCAST`) ADIMI engeller; aksi halde
  karakter yürür ve büyü sürer. `SPELLFLAG_NOFREEZEONCAST` bir büyüyü global
  bayraktan muaf tutar. Bizde `MovementEngine` her adımda
  `TryInterruptFromMovement` çağırıyordu ve iki bayrak da yüklenip hiç
  uygulanmıyordu. Canlı shard'da (MAGICFLAGS=0) bu, tek adımda büyü kaybı
  demekti. `IsMovementFrozenByCast` portlandı; hareket iptali kaldırıldı.

- [x] **İŞ-23-02 (P2) — `LOWERMANACOST` okunmuyordu.**
  Mana faturasından düşülen YÜZDE; negatif değer faturayı YÜKSELTİR
  (CResourceCalc.cpp:545-548, referansın kendi yorumu böyle diyor). Karakter +
  giyili eşya toplamı olarak okunuyor (`GetCastingPropertyValue` deseni).

- [x] **İŞ-23-03 (P2) — `LOWERREAGENTCOST` okunmuyordu.**
  Kaç tane harcanacağına dair indirim DEĞİL: o castın hiç reagent harcamama
  yüzde şansı (`:570`). Zar, varlık kontrolünü de kapsıyor — referans ikisini de
  tek fonksiyondan (`Calc_SpellReagentsConsume`, `fTest` parametreli) yapar, yani
  bedava bir cast harcamayacağı şeye sahip değil diye reddedilemez.

  **Paket tarafı ölçüm:** `Scripts-X-main/items/i_artifacts.scp` her ikisini de
  kullanıyor (`LowerManaCost=8/10`, `LowerReagentCost=40`) ve
  `core/defs_component_props.scp` ikisini AOS karakter property'si olarak
  kaydediyor.

### Kayıtlı sapmalar

- `[SPELL] @Select` / `@SpellSelect` argüman sözleşmesi eksik: referans ARGN2'de
  mana maliyetini verip GERİ OKUR, ARGN3'te bayrak (0x1 test / 0x2 failmsg),
  ARGO'da kaynak eşya, `LOCAL.TithingUse`; ayrıca `RETURN 0`/`RETURN 2` "kabul et
  ve kalan kontrolleri ATLA" anlamına gelir (CCharSpell.cpp:2367-2406). Bizde
  yalnız ARGN1/ARGN2 tohumlanıyor, geri okuma yok. **Ölçüm:** canlı paketin
  `[SPELL] ON=@Select` blokları yalnız `RETURN 1` kullanıyor
  (`sphere_spells.scp:1326`), yani sözleşmenin kalanının canlı tüketicisi yok.
- Tithing (paladin) sistemi yok; `Calc_SpellTithingCost` karşılığı eklenmedi.

**Testler:** `SpellCostAndFreezeParityTests` (10). Üç düzeltme tek tek geri alındı
ve sırasıyla 3 / 2 / 2 test kırmızıya döndü. Tam suite 3.438 / 0, üç arka arkaya
koşu.

**PLAN-403'ün kalanı:** alan etkisi, summon, seyahat.

---

## İŞ-24 — Büyü: bölge kapıları ve ölü bayraklar (PLAN-403 üçüncü dilim, 11 Eylül 2026)

### Kapanan boşluklar

- [x] **İŞ-24-01 (P1) — `CheckAntiMagic` portlanmamıştı.**
  Referans büyücünün bölgesine TEK bir soru sorar (CRegion.cpp:720) ve cevabı
  bizim baktığımız iki bitten çok daha geniş:

  | Bölge bayrağı | Reddettiği |
  |---|---|
  | `ANTIMAGIC_ALL` | her şey |
  | `ANTIMAGIC_RECALL_IN` **veya `REGION_FLAG_SHIP`** | Mark, Gate Travel |
  | `ANTIMAGIC_RECALL_OUT` | Recall, Gate Travel, **Mark** |
  | `ANTIMAGIC_GATE` | Gate Travel |
  | `ANTIMAGIC_TELEPORT` | Teleport |
  | `ANTIMAGIC_DAMAGE` | `SPELLFLAG_HARM` taşıyan her şey |

  En görünür iki sonuç: **Mark'ın bizde hiçbir bölge kapısı yoktu** (her yerde
  çalışıyordu) ve **gemi bölgesi de recall-in sorusunu yanıtlıyor**, yani
  referansta teknenin güvertesinde rune işaretlenemez. Red artık sessiz değil:
  `DEFMSG_MAGERY_6` mesajını veriyor.

- [x] **İŞ-24-02 (P2) — `MAGICF_SUMMONWALKCHECK` ölü bayraktı.**
  Çağrılan yaratık çağrıldığı yerde DURABİLMELİ (CCharSpell.cpp:2646). Bayrak
  `MagicConfigFlags` enum'unda ve ini'de duruyordu, hiçbir şey okumuyordu.
  Harita verisi yokken (birim testi dünyası) sessizce geçiyor: kontrol gerçek
  zemin bilgisine dayanır, uydurma bir cevap vermez.

- [x] **İŞ-24-03 (P2) — `MAGICF_OVERRIDEFIELDS` ölü bayraktı.**
  Yeni alan, karodaki mevcut büyü eşyalarının (IT_SPELL / IT_FIRE) YERİNİ alır
  (CCharSpell.cpp:2295). Sıradan bir eşyaya dokunmuyor.

### Kayıtlı sapmalar

- `MAGICF_NOFIELDSOVERWALLS` hâlâ okunmuyor. Referans bayrak açıkken alanın
  genişliğini merkezden dışa doğru duvarda KESER (CCharSpell.cpp:2187); bizim
  `CreateField` ise her karoyu tek tek geçilebilirlik için eliyor — bayraktan
  bağımsız ve her zaman. Davranış yakın ama kural aynı değil; kesme geometrisi
  ayrı bir iş.
- `ANTIMAGIC_TELEPORT` bizde HEDEF bölgede de kontrol ediliyor (referans yorumu
  "Can't teleport into here" diyor); `CheckAntiMagic` ise büyücünün bölgesine
  bakar. İkisi de korundu.

**Testler:** `SpellRegionAndFlagParityTests` (10). Üç düzeltme tek tek geri alındı
ve sırasıyla 2 / 1 / 1 test kırmızıya döndü. Tam suite 3.448 / 0, üç arka arkaya
koşu.

**PLAN-403 KAPANDI.** Sıradaki: PLAN-404 (skill/craft/gathering).

---

## İŞ-25 — Üretim: başarısızlık bedeli ve ACTIONEFFECT (PLAN-404 birinci dilim, 11 Eylül 2026)

### Ölçülüp DEĞİŞTİRİLMEYEN ayaklar

| Ayak | Kanıt |
|---|---|
| Toplama miktarı | `REAPAMOUNT` eğrisi; tanımsızsa `AMOUNT` eğrisi / 2, en az 1 ve damardaki stokla kırpılı (CCharSkill.cpp:1016-1027) |
| Tool aşınması | Her denemede (başarı ya da başarısızlık farketmez) |
| Stroke sayıları | Önceki dalgada kapandı (DELAY × sayı, SKF_GATHER tekrarı) |
| `@ResourceGather` / `@RegionResourceGather` | İŞ-9'da kapandı, `LOCAL.ResourceID` dahil |

### Kapanan boşluklar

- [x] **İŞ-25-01 (P2) — `ACTIONEFFECT` hiç yoktu.**
  Referansta `m_Act_Effect`: scriptin bir yetenek tetikleyicisi içinde yazdığı
  TEK SEFERLİK geçersiz-kılma değeri. Kullanıldığı yerler referansta iyileştirme
  miktarı (`:2882`), çekilen mana (`:2711`), alan yeteneğinin yarıçapı (`:1724`)
  ve başarısız üretimin malzeme payı (`:932`). Negatif yazım -1'e normalleşir
  (CChar.cpp:3730) ve her yetenek başlangıcı/temizliği -1'e döndürür
  (`:602`/`:4456`) — yani bir deneme sonrakini yönlendiremez; bu sıfırlama da
  eklendi.
  **Paket tarafı ölçüm:** canlı paket `dialogs/sphere_dialogs_prop.scp:603`'te
  `<ACTIONEFFECT>` basıyor, `:1036`'da `INPDLG ACTIONEFFECT` ile geri yazıyor ve
  `functions/sphere_functions.scp:265`'te hata ayıklama çıktısına koyuyor.

- [x] **İŞ-25-02 (P2) — Başarısız üretimin bedeli yalnız düz zardı.**
  Referansın sırası (CCharSkill.cpp:928-943): (1) `m_Act_Effect >= 0` ise o,
  (2) yoksa üretim yeteneğinin `EFFECT` eğrisinin rastgele değeri, (3) ikisi de
  yoksa `rand(50)`. Bizde yalnızca (3) vardı, yani kaybı ayarlayan bir paket yok
  sayılıyordu. **Canlı pakette Inscription `EFFECT=50`** (`sphere_skills.scp`),
  yani orada başarısız bir yazımın maliyeti fiilen yanlıştı.

### Kayda geçen — çözülmemiş test kırılganlığı

Bu dalga sırasında `SmeltRepairParity08ATests.AScriptMayChooseTheProduceAndTheYield`
**13 tam suite koşusunda 1 kez** düştü ve bir daha üretilemedi (aynı testin 6 ayrı
koşusu ve 10 tam suite koşusu temiz). Hata metni yakalanamadı. Testin tetikleyici
gövdesinde `Assert.Equal(1000, args.N1)` var (eritenin Mining yeteneği), yani
testler arası sızan bir duruma benziyor; bu dalganın değişiklikleriyle bir bağ
kurulamadı. **Açık madde olarak kayıtlı** — yeniden görülürse önce o iddia
incelenmeli.

**Testler:** `CraftFailureCostParityTests` (6). İki düzeltme tek tek geri alındı ve
sırasıyla 3 / 1 test kırmızıya döndü. Tam suite 3.454 / 0.

**PLAN-404'ün kalanı:** kaynak tükenmesi/yenilenmesi, retry, kalite.

---

## İŞ-26 — Üretim: kalite duyurusu ve usta imzası (PLAN-404 ikinci dilim, 11 Eylül 2026)

### Ölçülüp DEĞİŞTİRİLMEYEN

Kalite formülünün **tamamı** referansla birebir çıktı (CCharSkill.cpp:724-794):
`2 - log10(1 + rand(250))` sapması, %50 işaret çevirme, `skill*2/10` ile yedi
bant seçimi, bant sınırlarının kırpılması ve bant içi zar. Dokunulmadı.

### Kapanan boşluklar

- [x] **İŞ-26-01 (P2) — `MAKESUCCESS_1..6` hiç gönderilmiyordu.**
  Referans bandı zar atarken adlandırır ve ustaya söyler (`:758-788`); yalnız
  ORTALAMA bandı sessizdir (`:771`). Altı mesaj anahtarı bu motorun tablosunda
  duruyordu ve hiçbiri gönderilmiyordu. Bant, nihai kalite DEĞERİNDEN türetiliyor
  (sınırlar aynı), böylece zarın içinden bir değer taşımaya gerek kalmadı.

- [x] **İŞ-26-02 (P2) — `OF_NOITEMNAMING` ölü bayraktı.**
  Referans usta imzası için üç şey ister: büyük usta (skill > 999), kalite > 175
  VE bayrağın kapalı olması (`:799`). İlk ikisi bizde vardı, üçüncüsü yoktu.
  Kural `CraftingEngine.EarnsMakersMark` olarak ayrıldı ki sınanabilsin.
  Canlı ini `OPTIONFLAGS=0x2080`, yani bu bit orada kapalı — canlı etkisi yok,
  ama "shard yazabiliyor, motor okumuyor" sınıfının bir üyesiydi.

### İŞ-25'in açık maddesi KAPANDI

`SmeltRepairParity08ATests.AScriptMayChooseTheProduceAndTheYield` kırılganlığının
sebebi bulundu: test, sınıfın kendi `SkillRolls` yardımcısını **çağırmıyordu**.
Mining 100.0'da da çan eğrisi çizilir, yani eritme arada bir başarısız olur ve
hiç külçe çıkmaz. Testin konusu ürünün ne olduğu, zarın nasıl düştüğü değil;
artık dosyadaki diğer testler gibi zarı sabitliyor.

**Testler:** `CraftQualityMessageParityTests` (18). `OF_NOITEMNAMING` kapısı geri
alınıp yakalandığı doğrulandı. Tam suite 3.472 / 0, dört arka arkaya koşu.

**PLAN-404'ün kalanı:** kaynak tükenmesi/yenilenmesi, retry.

---

## İŞ-27 — Kaynak: boş çıkan nokta boş kalır (PLAN-404 üçüncü dilim, 11 Eylül 2026)

### Ölçülüp DEĞİŞTİRİLMEYEN ayaklar

| Ayak | Kanıt |
|---|---|
| Damar ömrü | `REGEN` tenths; tek pencere, dokunulunca yeniden kurulmaz (05C dalgasında kapandı) |
| Havuz miktarı | `AMOUNT` eğrisinin RASTGELE değeri + `RACIALF_HUMAN_WORKHORSE` bonusu |
| Reap miktarı | `REAPAMOUNT`, yoksa `AMOUNT`/2, stokla kırpılı |
| Tükenmiş damar | Havuz 0 → harcanmış sayılır (CCharSkill.cpp:1456) |
| `@ResourceFound` / `@ResourceGather` | İŞ-9'da kapandı |

### Kapanan boşluk

- [x] **İŞ-27-01 (P1) — `mr_nothing` çekilişi düğüme yazılmıyordu.**
  Referans `CheckNaturalResource`'ta grubun cevabı `mr_nothing` olsa BILE bit'i
  oluşturur (`:119` → `:131`) ve çözülme süresini o tanımın `REGEN`'inden kurar
  (`:148`); toplama ancak sonra, `Skill_NaturalResource_Create` içinde reddeder
  çünkü tanım hiçbir şey vermez (CCharSkill.cpp:1012). Yani boş sonuç o kareye
  düğümün ömrü boyunca BAĞLANIR.

  Bizde boş çekiliş atılıyordu (kodun kendi yorumu: "never persisted on a node" —
  referans alıntısı olmayan bir varsayım). Sonuç: her vuruşta grup yeniden
  çekiliyordu.

  **Paket tarafı ölçüm:** canlı paketin `sphere_region.scp:35-37`'si
  `[REGIONRESOURCE mr_nothing] REAP=i_unused REGEN=60*60*10` diyor ve yanına
  **"Nothing can be found at this location for this many seconds"** yazıyor;
  `:88` ve `:95`'te su için çekiliş ağırlığı **%60**. Yani paketin yazarı tam
  olarak bu kalıcılığa güveniyor: bizim davranışımızda bir balıkçı tek karede
  durup o %60'ı her atışta yeniden çekebiliyordu.

**Testler:** `BarrenResourceNodeParityTests` (3). Düzeltme geri alındığında üçü de
kırmızıya döndü. Tam suite 3.475 / 0, üç arka arkaya koşu.

**PLAN-404 KAPANDI.** Sıradaki: PLAN-405 (pet/mount/stable).

---

## İŞ-28 — Pet: takipçi sınırını kim bağlar (PLAN-405 birinci dilim, 11 Eylül 2026)

### Ölçülüp DEĞİŞTİRİLMEYEN ayaklar

| Ayak | Kanıt |
|---|---|
| Slot maliyeti | `FOLLOWERSLOTS` chardef'ten, varsayılan 1 (CCharUse.cpp:1210) |
| Mevcut toplam | `CURFOLLOWER` slot toplamı olarak, canlı taramayla |
| Azami | `MAXFOLLOWER` sahipte |
| Kontrol noktaları | Sahiplik atama, ahırdan geri alma, figürinden çıkarma |

### Kapanan boşluklar

- [x] **İŞ-28-01 (P1) — `OF_PETSLOTS` hiç okunmuyordu.**
  Referans slot sisteminin tamamını o bayrağın arkasında tutar: çağırma yolu
  (`CCharSpell.cpp:2660`), evcil hafıza defteri (`CChar.cpp:1257`) ve
  `FollowersUpdate`'in kendisi ("This is supossed to be called only when
  OF_PetSlots is enabled", CCharUse.cpp:1236). Bizde bayrak `OptionFlags`
  enum'unda duruyor ve hiçbir yer okumuyordu, yani sınır KOŞULSUZ uygulanıyordu.
  **Canlı shard bunu kapatmış:** `OPTIONFLAGS=0x2080`, 0x10 biti yok.

- [x] **İŞ-28-02 (P2) — GM azami sayıyı aşamıyordu.**
  Referans `fIgnoreMax = IsPriv(PRIV_GM)` (CCharUse.cpp:1238). İki kontrol
  noktası da (sahiplik, ahır) artık tek kapıdan geçiyor:
  `Character.FollowerCapApplies` — kopyaların ayrışması böylece imkânsız.

### Test tarafı

Sınırı sınayan 8 dosyadaki 22 kurulum noktası artık bayrağı **kendisi bildiriyor**
(`ServerOptionFlags |= PetSlots`). Bu bilinçli: `MaxFollower` atayan bir test
zaten sınır hakkında bir testtir, ve sınır ancak bayrak açıkken bağlar.

### Kayıtlı sapma

- `@FollowersUpdate` tetikleyicisi yok (referansta ARGN1 ekle/çıkar, ARGN2 slot
  sayısı, ARGO evcil; RETURN 1 reddeder). Ne canlı pakette ne `oldSphere/`
  paketlerinde tek bir kancası var — İŞ-8'in paket-tarafı ölçüm kuralı gereği
  eklenmedi, kayda geçti.

**Testler:** `FollowerCapGateParityTests` (4). Kapı geri alındığında üçü kırmızıya
döndü. Tam suite 3.479 / 0, üç arka arkaya koşu.

**PLAN-405'in kalanı:** sahiplik değişimi, park/geri alma, ölüm, logout/relogin.

---

## İŞ-29 — Pet: görevden alınan satıcı neyi geri verir (PLAN-405 ikinci dilim, 11 Eylül 2026)

### Ölçülüp DEĞİŞTİRİLMEYEN

Sahiplik devri zaten doğruydu: gerçek bir sahip değişimi eski sahibin
`MEMORY_IPET`/`MEMORY_FRIEND` ilişkilerini ve bond'u temizliyor
(`NPC_PetSetOwner` → `NPC_PetClearOwners`, CCharNPCPet.cpp:600/558) ve
"sahipsiz pet bonded olamaz" kuralı uygulanıyor.

### Kapanan boşluk

- [x] **İŞ-29-01 (P1) — Serbest bırakılan oyuncu satıcısı elindekini götürüyordu.**
  Referans `NPC_PetClearOwners`, satıcı sahibini kaybettiği anda
  (CCharNPCPet.cpp:562-584): `STATF_INVUL`'ü düşürür, banka kasasındaki parayı
  sahibin bankasına aktarır (`AddGoldToPack(..., pBankOwner)`) ve **her satıcı
  katmanı kabının** içindekileri sahibin bankasına boşaltır.

  Bizde `ClearOwnership` yalnızca sahiplik bayraklarını temizliyordu: dükkâncının
  `VENDOR_GOLD` kasası ve oyunculardan satın aldığı mallar (VendorExtra) NPC'nin
  üzerinde sahipsiz kalıyordu — oyuncu için kayıp.

  Geri verme `Trade.VendorEngine.ReturnHoldingsToOwner` içinde ve `ClearOwnership`
  sahiplik alanlarını sıfırlamadan ÖNCE çağrılıyor; sonra çağrılsa sahip artık
  bilinmiyor olurdu.

### Bilinçli sapma (kayıtlı)

Referans **bütün** satıcı katmanlarını boşaltır; biz yalnız **VendorExtra**'yı
veriyoruz. Sebebi yapısal: bu motorda SELL stoku (LAYER 26) sanaldır — SELL
şablonundan talep üzerine kurulur ve `WorldSaver` onu hiç kaydetmez. Onu "geri
vermek" eşya iadesi değil eşya BASMAK olurdu. VendorExtra ise gerçek: satıcının
oyunculardan aldıkları orada durur.

**Testler:** `VendorDismissalParityTests` (5) — banka yoksa malın sahibin ayağına
düşmesi ve sıradan bir pet'in bu yolu hiç görmemesi dahil. Kapı geri alındığında
dördü kırmızıya döndü. Tam suite 3.484 / 0, üç arka arkaya koşu.

**PLAN-405'in kalanı:** park/geri alma, ölüm, logout/relogin.

---

## İŞ-30 — Pet/ölüm/logout: PLAN-405'in kalanı (11 Eylül 2026)

PLAN-405'in son üç ayağı: **park/geri alma, ölüm, logout/relogin.**

### Ölçülüp DEĞİŞTİRİLMEYEN ayaklar

| Ayak | Kanıt |
|---|---|
| Ölümde binekten inme sırası | Ceset oluşmadan önce (CCharAct.cpp:4399) — zaten böyle |
| Bonded pet ölümü | Hayalet olarak kalır, silinmez, sahipliği düşmez (:4427) |
| Bonded olmayan NPC ölümü | Sahiplik temizlenir + silinir (:4437) |
| Ölümde saldıran listesi | `m_lastAttackers.clear()` (:4420) — zaten temizleniyor |
| Çıkışta takas penceresi | `ClientDetach` siler (CChar.cpp:487-492) — `AbortActiveTradeOnDisconnect` |
| Çıkışta grup | `SetDisconnected` üyeliği düşürür (:542) — `LeavePartyOnDisconnect` |
| Park mekanizması | `Make_Figurine` pet'i silmez, park eder — `PetStorage.Park` aynısını yapar |

### Kapanan boşluklar

- [x] **İŞ-30-01 (P1) — Hayalet kapıdan geçemiyordu.**
  Referans her ÖLÜ karaktere `CAN_C_GHOST` verir (`GetCanMoveFlags`,
  CCharStatus.cpp:739) ve bu bayrak kapı karosundan `CAN_I_BLOCK`'u düşürür
  (CChar.cpp:760). Bizde ölü bayrağı hareket geometrisinde hiç okunmuyordu.
  **Canlıda etkin:** kapısı kapanmış bir odada ölen oyuncu içeride kilitli
  kalıyordu ve bu sunucu kapıları 20 saniye sonra kendisi kapatıyor.
  Kapsam kasten dar: yalnız KAPI — duvar `CAN_C_PASSWALLS` ister, onu ölüm
  vermez. Kapı eşyaları hem `ItemType.Door/DoorLocked` hem `TileFlag.Door`
  ile tanınıyor (referansın `CAN_I_DOOR` işaretlediği küme).

- [x] **İŞ-30-02 (P1) — Berserk yaratık sahibini terk ediyordu.**
  Referans `NPC_PetDesert`'in kendisini korur (CCharNPCPet.cpp:906) ve sebebi
  yorumda yazılı: berserk bir çağırma `CURFOLLOWER`'a sayılır, dolayısıyla ona
  vurmak terk ettirseydi oyuncu kendi çağırdığına vurup slotu boşaltır ve
  bedelsiz bir yenisini çağırırdı. Bizdeki iki terk yolu da korumasızdı:
  sahibin kendi vuruşu (`ClientCombatHandler`, referansta CCharFight.cpp:313)
  ve açlık tiki (referansta CCharAct.cpp:5791).
  **Paket tarafı ölçüm:** canlı paketin `c_vortex` (Energy Vortex) ve
  `c_blade_spirit` (Blade Spirit) kayıtlarının ikisi de `brain_berserk` —
  referansın adını koyduğu çiftin ta kendisi. Bizde `CurFollower` canlı
  taramayla sahipli her yaratığı sayıyor, yani sömürü kurulabilirdi.

- [x] **İŞ-30-03 (P2) — Çıkış yapan denizcinin gemisi yola devam ediyordu.**
  `CChar::ClientDetach`: "If this char is on a IT_SHIP then we need to stop the
  ship!" (CChar.cpp:497-502). Bizde `OnDisconnect` gemiye hiç bakmıyordu.

- [x] **İŞ-30-04 (P2) — Ahır kapasitesi: yanlış eğri, yanlış karakter.**
  İki ayrı hata (`NPC_StablePetSelect`, CCharNPCAct_Vendor.cpp:118-169):
  1. `MAXPLAYERPETS` **ahırcının** etiketidir (referans kendi `m_TagDefs`'ini
     okur, :124); biz sahipten okuyorduk. Referans paketin kendi belgesi de
     böyle diyor: "Stablers ... can be overriden with a TAG.MAXPLAYERPETS"
     (`oldSphere/Scripts-X-main/npcs/README.txt:144`). Ahırcısını ayarlamış bir
     shard hiçbir şey elde edemiyordu.
  2. Etiket yokken sayı, Taming+Animal Lore+Veterinary toplamına bağlı bir
     **kademedir** (<160.0 → 2, ≥160.0 → 3, ≥200.0 → 4, ≥240.0 → 5), sonra
     üç beceriden 100.0 ve üzerindeki her biri `(beceri - 90.0)` slot ekler.
     Bizdeki "sabit 5 taban + toplam/60.0" uydurmaydı: eğitimsiz sahip 5
     alıyordu (referansta 2), GM-üçlü 10 alıyordu (referansta 95).
  Ayrıca ahır bir kaptır: kap eşya sınırı önce sınırlar (:112).

### Kayıtlı sapmalar

- **Ölen NPC binici.** Referansta binekten inme yalnızca OYUNCUYA uygulanır
  ("if I'm NPC then my mount goes with me", CCharAct.cpp:4399); bizde koşulsuz.
  Fark ancak binekli bir NPC varsa görünür ve ne canlı pakette ne
  `oldSphere/` paketlerinde binekli NPC tanımı var — İŞ-8'in paket-tarafı
  ölçüm kuralı gereği dokunulmadı, kayda geçti.
- **`MAXPLAYERPETS` sahipte.** Artık okunmuyor. Referans hiç desteklemedi ve
  iki pakette de tek bir kullanımı yok; yine de davranış değişikliği.

### Test altyapısı

`Item.ResolveShipEngine` `ResetEngineStatics`'e eklendi — testler onu elle
kaydedip geri yüklüyordu, ki proje bu sıra-bağımlı kırılganlığı bir kez çözdü.

**Testler:** `GhostDoorPassageParityTests` (4), `PetDesertAndLogoutParityTests` (6),
`VendorStableParityTests` genişletildi (+7). Dört düzeltme tek tek geri alındı;
sırasıyla 2, 2, 1 ve 4 test kırmızıya döndü. Tam suite 3.501 / 0, üç arka arkaya
koşu.

**PLAN-405 kapandı.** Sıradaki: PLAN-406 (death/corpse/loot).

---

## İŞ-31 — Death/corpse/loot (PLAN-406, 11 Eylül 2026)

PLAN-406'nın ayakları: imleçteki eşya, newbie/blessed, ceset-birleşme (rejoin),
çürüme, suç/tanık/muhafiz ve kayıt.

### Ölçülüp DEĞİŞTİRİLMEYEN ayaklar

| Ayak | Kanıt |
|---|---|
| İmleçteki eşya | `LAYER_DRAGGING` cesede gider (CCharAct.cpp:636) — `TakeDraggedItem` |
| Saç/sakal | Cesede **kopya** çıkar, aslı hayalette kalır (:625-634) — `CORPSE_HAIR` |
| newbie/blessed | Cesede değil **çantaya** gider (:651/:664) — `KeepWithOwner` |
| Çanta ile giyim farklı küme | `ContentsTransfer` dar, `UnEquipAllItems` geniş — iki ayrı yüklem |
| Ceset-birleşme | Önce giydir, sonra çantaya, saç/sakal atla (CItemCorpse.cpp:245-262) |
| Çürüme | `CORPSENPCDECAY` / `CORPSEPLAYERDECAY`, ini'den (CServerConfig.cpp:826) |
| Katil damgası | `m_uidKiller` (:205) — `KILLER_UID`, adli tıp okuyor |
| Oyulamaz ceset | bonded/summon/uyuyan (:215) — `CORPSE_CARVED` |
| Suç kapısı | `LOOTINGISACRIME` + sahip yaşıyor + masum (:120-127) |
| Kayıt | `MODMAXWEIGHT` save/load turunu tamamlıyor (`WorldSaver` — `TrySetProperty`) |

### Kapanan boşluklar

- [x] **İŞ-31-01 (P1) — Ölüm büyü etkilerini bitirmiyordu.**
  Referans `CChar::Death`, ceset oluşmadan önce `Spell_Dispel(100)` çalıştırır
  (CCharAct.cpp:4397, yorumu: "get rid of all spell effects"). Bizde
  `StripDispellableEffects` vardı ama yalnızca Dispel büyüsü çağırıyordu:
  büyülenmiş ölen oyuncu büyülü diriliyor, lanetli ölen lanetli diriliyordu.
  Referans `ATTR_MOVE_NEVER`'lı büyü-katmanı eşyalarını bağışlar; bu motor o
  katmanlara kendi etki hafızasından başka bir şey koymuyor, yani bağışlanacak
  bir şey yok — uydurma bir özellik kontrolü eklenmedi.

- [x] **İŞ-31-02 (P2) — Cesedin ağırlık sınırı yoktu.**
  Referans `MakeCorpse`, `m_ModMaxWeight = Calc_MaxCarryWeight(this)` atar ve
  sebebini açıkça yazar (CItemCorpse.cpp:194): birinin bir oyuncu cesedine, o
  oyuncu dirilirken sıkışıp kalsın diye bir sürü eşya doldurmasını önlemek.
  Bu proje bunu bir kez **ertelemişti** (death/corpse denetimi D1-D3).
  Düzeltme tek satır: bırakma yolu bir kabın `MODMAXWEIGHT`'ini zaten uyguluyor
  (`ClientInventoryHandler`), cesedin öylesi yoktu.

- [x] **İŞ-31-03 (P2) — Ceset suçu tanık hattını hiç çalıştırmıyordu.**
  `CheckCorpseCrime`, eylem suçsa **iki** şey yapar (CItemCorpse.cpp:132-133):
  cesedin sahibini `pCharMark` yaparak `CheckCrimeSeen(SKILL_NONE, ...)`
  çalıştırır, **sonra** `Noto_Criminal()`. Bizde yalnızca ikincisi vardı:
  `CrimeWitnessService.CheckCrimeSeen` yazılmış ve Çalışıyordu ama sadece
  Hırsızlık/Gizlice-bakma çağırıyordu. Sonuç: izleyenler `MEMORY_SAWCRIME`
  kaydetmiyordu (yağmacı onlara gri görünmüyordu), korumalı bölgedeki NPC
  muhafiz çağırmıyordu ve `@SeeCrime` hiç ateşlenmiyordu. İki çağırım noktası
  (yağma ve parçalama) tek kapıya bağlandı: `DeathEngine.ReportCorpseCrime`.
  `SKILL_NONE` bilinçli: yağma örtülü bir eylem değil, algı yarışı yok.

### Kayıtlı sapma — yüksek ATTR bitleri

Referansın modern `attr_flags` tablosu (Scripts-X `core/defs.scp:172-200`) ile
`ObjAttributes` üç bitte ayrışıyor:

| Bit | Referans | Bizde |
|---|---|---|
| 0x40000 | `attr_imbued` | `Secure` |
| 0x80000 | `attr_questitem` | `DamageD` |
| 0x100000 | `attr_insured` | `LockedDown` |
| 0x1000000 | `attr_lockeddown` | (yok) |
| 0x2000000 | `attr_secure` | (yok) |

Bu yüzden `UnEquipAllItems`'ın koruma kümesindeki `ATTR_INSURED` ve
`ATTR_QUESTITEM` temsil edilemiyor. **Bilerek değiştirilmedi:** ATTR sayısal
olarak kaydediliyor, yani `LockedDown`'ı 0x100000'den 0x1000000'e taşımak canlı
shard'ın kayıtlı her kilitli eşyasını yanlış yorumlardı — CLAUDE.md'nin
"save/load biçimini bozma" kuralı. Paket tarafı ölçüm de aciliyeti düşürüyor:
canlı pakette tek kullanım yorum satırında, `Scripts-X-main`'de hiç yok, ve
canlı paketin kendi `attr_flags` tablosu zaten 0x8000'de bitiyor.

**Testler:** `DeathDispelAndCorpseWeightParityTests` (5),
`CorpseCrimeWitnessParityTests` (5). Üç düzeltme tek tek geri alındı; sırasıyla
2, 2 ve 2 test kırmızıya döndü. Tam suite 3.511 / 0, üç arka arkaya koşu.

**PLAN-406 kapandı.** Sıradaki: Dalga 5 (PLAN-501 — housing/ship/social).

---

## İŞ-32 — Konut: öneksiz anahtarlar ve taşıma sandığı (PLAN-501, 12 Eylül 2026)

### Paket tarafı ölçüm

Konut API'sinin gerçekte kullanılıp kullanılmadığını önce paketlerden ölçtüm.
**Canlı paket hiçbirini kullanmıyor** (tek "addons" eşleşmesi bir `CATEGORY`
metni). **`Scripts-X-main` ise yoğun kullanıyor:** `BASESTORAGE` 163, `REDEED` 35,
`LOCKDOWNS` 13, `MOVINGCRATE` 10, `INCREASEDSTORAGE` 10, `SECURED` 6,
`MAXSTORAGE` 6, `UNLOCKITEM` 4, `MAXVENDORS` 3, `COOWNERS` 1.

Bunların hangisinin bizde karşılığı olduğunu saydım: `MOVINGCRATE` **sıfır**
geçiyordu. `SECURED`/`ADDONS` eşleşmeleri ise çalışma-anı okuması değil
(`CATEGORY` metinleri ve sysmessage'lar) — o yüzden işe alınmadılar.

### Kapanan boşluklar

- [x] **İŞ-32-01 (P1) — Konut anahtarları yalnızca-yazılırdı.**
  Referans bu anahtarları multi'nin **üzerinde öneksiz** tutar
  (`CItemMulti::r_WriteVal`, SHL_* tablosu) ve paketler de öyle okur
  (`<argo.movingcrate>`). Bizde `TrySetProperty` öneksiz biçimi kabul ediyor ve
  değeri canlı eve yazıyordu (`Item.cs`, `BASESTORAGE`/`INCREASEDSTORAGE`/
  `LOCKDOWNSPERCENT`/`BASEVENDORS`), ama `TryGetProperty` yalnızca `HOUSE.`
  önekliyi çözüyordu. Ölçtüm: `BASESTORAGE=999` → `house.BaseStorage == 999`,
  `<BASESTORAGE>` → `ok=False`. Yani bir script kendi yazdığını geri okuyamıyordu.

  **Kapsam kasten dar:** konut araması eşyanın kendi anahtarlarından **sonra**
  koşuyor, böylece ortak anahtarlar (`TYPE`, `OWNER`) hala EŞYANIN değerini
  veriyor; ve `strict` kipinde ev yoksa / anahtar tanınmıyorsa **false**
  dönüyor. Bu önemliydi: `HOUSE.` yolunda tanınmayan anahtar "0" dönüyor, o
  davranış öneksiz yola taşınsaydı **her eşyada her bilinmeyen anahtar 0**
  olurdu.

- [x] **İŞ-32-02 (P2) — `MOVINGCRATE` hiç yoktu.**
  Referans taşıma sandığını multi'nin üzerinde tutar (`_uidMovingCrate`) — bu,
  yalnızca redeed sırasında var olan bir şey değil. Bizde yalnızca redeed anında
  üretilen geçici sandıklar vardı.

  | Parça | Referans |
  |---|---|
  | `GetMovingCrate(fCreate)` | ITEMID_CRATE1, evin noktasında **Z-20**, eve link (:1329) |
  | `SetMovingCrate` | dolu eski sandığı yenisine boşaltır, eskisini siler (:1317) |
  | `<MOVINGCRATE>` | uid ya da 0; **yaratmaz** (:2876) |
  | `<MOVINGCRATE 1>` | gerekirse yaratır, sonra uid (:2872) |
  | `<MOVINGCRATE.key>` | alt-anahtarı sandığa iletir (:2866) |
  | `MOVINGCRATE=1` (yükleme) | "bir tane yarat" (:3034) |
  | Kayıt | ev bir tane taşıyorsa yazılır (:2666) |

- [x] **İŞ-32-03 (P2) — Redeed eldeki sandığı terk ediyordu.**
  Referans `TransferAllItemsToMovingCrate`, `GetMovingCrate(true)` sorar — yani
  evin zaten taşıdığı sandık malları alan sandıktır. Yeni sandık mantığı artık
  yalnızca kap sınırını aşan taşma için çalışıyor. Ayrıca sandık, paketlenecek
  başka bir şey **olmasa da** evden alınıyor; yoksa içi dolu bir sandık artık var
  olmayan bir evin altında (Z-20) gömülü kalırdı.

**Testler:** `HouseMovingCrateParityTests` (12) — ev olmayan bir eşyanın hiçbir
konut anahtarına yanıt vermemesi ve `TYPE`'ın hala eşyanınki olması dahil. İki
düzeltme tek tek geri alındı; her biri 6 testi kırmızıya döndürdü. Tam suite
3.523 / 0, üç arka arkaya koşu.

**PLAN-501'in kalanı:** component kimliği/ekle-sil, addon ilişkileri,
secure/lockdown, house vendor, key.

---

## İŞ-33 — Konut: izin anahtarları ve işaret olayları (PLAN-501 ikinci dilim, 12 Eylül 2026)

### Yöntem

Referansın multi anahtar tablosunu (`CItemMulti::sm_szLoadKeys`, 44 anahtar)
bizim karşılıklarımızla diff'ledim: **18 anahtar eksikti.** Sonra her birini iki
paketten ölçtüm ve yalnızca gerçekten kullanılanları işe aldım.

| Anahtar | Scripts-X | Canlı | Karar |
|---|---|---|---|
| `ISOWNER` | 45 | 0 | eklendi |
| `GETCOOWNERPOS` | 32 | 0 | eklendi |
| `GETFRIENDPOS` | 29 | 0 | eklendi |
| `GETSECUREDITEMS` | 3 | 0 | eklendi |
| `GETACCESSPOS` | 2 | 0 | eklendi |
| `GETBANPOS` | 2 | 0 | eklendi |
| `GETSECUREDCONTAINERPOS` | 1 | 0 | eklendi |
| `ADDONS` | 9 | 4 | **eşleşmeler `CATEGORY` metni** — alınmadı |
| `ADDADDON` `ADDKEY` `ADDVENDOR` `REMOVEKEYS` `GETADDONPOS` `ADDCOMPONENT` | 0 | 0 | kayıtta |

`GETCOMPPOS`, `GETHOUSEVENDORPOS`, `GETLOCKEDITEMPOS` ve
`GETSECUREDCONTAINERS` paket ölçümünde sıfır çıktı ama **yine de eklendi**:
aynı paylaşılan yardımcının tek satırlık çağırımları ve referans onları aynı
tabloda aynı biçimde tanımlıyor — aileyi keyfi olarak bölmek daha kötü olurdu.
Yazan tarafları (`ADDADDON` vb.) eklenmedi.

### Kapanan boşluklar

- [x] **İŞ-33-01 (P1) — Konut izin kapısı hiç yanıt vermiyordu.**
  Scripts-X konut paketi izni şöyle sorar:
  `if (<isowner <src>>) || (<GetCoownerPos <src>> ≥ 0) || (<GetFriendPos <src>> ≥ 0)`.
  Bizde bu anahtarların hiçbiri yoktu — yani o paketin **bütün** konut izin
  sınamaları boş okuyordu.

  **Sözleşme ayrıntısı:** listede olmayan bir uid için **-1** dönmeli, 0 değil;
  paketler tam olarak `≥ 0` sınıyor, yani 0 dönmek "listede" demek olurdu.
  Dönen konum, `COOWNER.n` gibi indeksli okumayla aynı sırayı yürüyor.

  **`GETSECUREDCONTAINERS` vs `GETSECUREDITEMS`:** referans ikisini ayırır
  (CItemMulti.cpp:1904/1919) — biri kap sayısı, diğeri o kapların içindeki eşya
  sayısı. Konut diyalogu depolama bütçesini ikincisiyle harcar.

- [x] **İŞ-33-02 (P1) — Kilit/güvence işaret olayı konmuyordu.**
  Referans kilitlerken/güvenceye alırken eşyaya `EVENTS +ei_house_lockdown` /
  `+ei_house_secure` ekler (:1779/:1856).

  **Bunu önce sapma olarak kaydedecektim** ("paket bu event'i tanımlamıyor"),
  doğrulayınca yanıldığımı gördüm. Bunlar bir script değil, bir **işarettir**:
  Scripts-X konut paketi bir evi boşaltırken her eşyaya
  `<isevent.ei_house_lockdown>` diye sorar ve sonra `ref1.unlockitem <uid>`
  çağırır (`house_functions.scp:297/881/885/927/930`). İşaret konmadan o
  paketteki hiçbir şey kilitli eşyayı boşta durandan ayıramıyordu — ev
  boşaltma akışı sessizce hiçbir şey yapmazdı. Bırakma işareti geri kaldırıyor.

### Ölçülüp DEĞİŞTİRİLMEYEN

| Ayak | Kanıt |
|---|---|
| `GetCurrentStorage` | `lockdowns + secureContainers` (CItemMulti.cpp:1728) — aynı |
| `GetMaxStorage` | `base + base*increased/100` (:1723) — aynı |
| `GetMaxLockdowns` | ifade `maxStorage*pct/100`'e indirgenir (:1749) — aynı |
| `GetMaxVendors` | `baseVendors + baseVendors*increased/100` (:1743) — aynı |
| `LockItem` kapısı | zaten kilitli **veya** zaten güvencede ise reddeder (:1773) — aynı |
| `Secure` kapısı | zaten güvencedeyse reddeder (:1850) — aynı |
| Kilit/güvence eşya tarafı | `ATTR_LOCKEDDOWN`/`ATTR_SECURE` + eve link — aynı |

**Testler:** `HousePermissionKeyParityTests` (17) — ev olmayan bir eşyanın
hiçbirine yanıt vermemesi, dönen konumun indeksli okumayla uyuşması ve iki
işaretin birbirine karışmaması dahil. Anahtar kapısı geri alındığında 14, işaret
olayları geri alındığında 2 test kırmızıya döndü. Tam suite 3.540 / 0, üç arka
arkaya koşu.

**PLAN-501 kapandı.** Sıradaki: PLAN-502 (MOVEALLTOCRATE/MOVELOCKSTOCRATE).

---

## İŞ-34 — Ev yıkımı: elindekini bırakmak (PLAN-502, 12 Eylül 2026)

### Önce kapsam: fiiller yazılmadı

PLAN-502 adıyla `MOVEALLTOCRATE` / `MOVELOCKSTOCRATE` senaryolarını istiyor.
Paket ölçümü: **her iki fiil de, `MOVECRATETOBANK` ve `REDEEDADDONS` de, iki
pakette de sıfır kullanım.** `TRANSFER_*` sabitlerinin tek eşleşmesi
`core/defs.scp`'teki tanımların kendisi. `@Redeed`'in ARGN2/ARGN3 geri-okuması
da kullanılmıyor: Scripts-X redeed'i **çıplak `REDEED` fiiliyle** sürüyor
(`house_dialogs.scp:21` ve `:1046`) ve sandık işini `f_house_box` ile script
tarafında kendisi yapıyor.

İŞ-8'in kuralı gereği bunlar yazılmadı. Bunun yerine dalganın **kabul ölçütüne**
gidildi: "ev/gemi silme veya redeed sırasında içerik kaybolmaz; taşınan nesne iki
parent altında görünmez." Var olan yollarda bu ölçütün gerçekten kırıldığı
üç yer bulundu.

### Kapanan boşluklar

- [x] **İŞ-34-01 (P1) — Silinen ev kilitlediklerini bırakmıyordu.**
  Bu motorda kilitli bir eşya **taşınamaz**: `ObjAttributes.LockedDown`,
  `Item.IsMovableType`'ı düşürür. Dolayısıyla silinen bir evin geride bıraktığı
  kilitli eşya dünyada **temelli** sıkışıyordu: artık var olmayan bir multi'ye
  baglı, sahibi tarafından alınamaz. Referans `UnlockAllItems` ile bırakır
  (CItemMulti.cpp:1804).

  Bizde temizleme **yalnızca REDEED yolunda** vardı; GM `.remove`'u, script
  `REMOVE`'u ya da başka herhangi bir yıkım — hepsi `OnWorldObjectDeleting`'e
  düşüyor ve orada kilit/güvence hiç ele alınmıyordu. İŞ-33'te eklenen işaret
  olayı da üzerlerinde kalıyordu.

- [x] **İŞ-34-02 (P1) — Taşıma sandığı evin altında gömülü kalıyordu.**
  Sandık ev Z'sinin **20 altında** durur (İŞ-32). Ev silindiğinde sandık orada,
  içindekilerle kalıyordu. Referans `TransferMovingCrateToBank` ile devreder
  (:1522). Banka yoksa yere düşüyor, hiçliğe değil.

- [x] **İŞ-34-03 (P2) — Boş sandık bankaya postalanıyordu.**
  Referans boş sandığı teslim etmez, **siler** (:1539). Hem yıkım hem redeed
  yolunda uygulandı — İŞ-32'de redeed artık eldeki sandığı kullandığı için
  dokunulmamış bir sandık taşıyan ev aksi halde bankaya boş kutu koyardı.

### Tek-parent değişmezi

Sandık yerden bankaya geçerken önce sektörden düşürülüyor
(`HideFromSector`), yani aynı anda hem dünyada hem kapta görünmüyor. Bunu
doğrulayan ayrı bir test var (`ADeliveredCrateHasExactlyOneParent`).

### Ölçülüp DEĞİŞTİRİLMEYEN

| Ayak | Kanıt |
|---|---|
| Tekrar çağırım | Referans `REMOVED` anahtarıyla korur (:1198); bizde `_redeeded` + kayıttan silme — ikinci deed üretilemiyor |
| Bileşen temizliği | `RemoveAllComponents` (:144) — `OnWorldObjectDeleting` zaten siliyor |
| Dolu hedef | Banka almazsa yere, `TryAddItem` dönüşü kontrol ediliyor |

**Testler:** `HouseTeardownHoldingsParityTests` (8). İki düzeltme tek tek geri
alındı; sırasıyla 5 ve 1 test kırmızıya döndü. Tam suite 3.548 / 0, üç arka
arkaya koşu.

**PLAN-502 kapandı.** Sıradaki: PLAN-503 (custom housing commit + harita
yürüme geometrisi + save/reload).

---

## İŞ-35 — Tasarım commit'i: fiyatlanabilir ve reddedilebilir (PLAN-503, 12 Eylül 2026)

### Ölçülüp DEĞİŞTİRİLMEYEN bağlar

PLAN-503 commit doğrulamasını **harita yürüme geometrisi** ve **save/reload** ile
bağlamayı istiyor. İkisi de zaten bağlıydı:

| Bağ | Kanıt |
|---|---|
| Yürüme geometrisi | `WalkCheck.ResolveCustomDesign` → `GetCommittedTiles` (`Program.EngineWiring.cs:2750`); `GameWorld` de aynı kancayı kullanıyor |
| Revizyon önbelleği | `GetCommittedDesign`, `DESIGN_REVISION` değişince yeniden ayrıştırıyor — commit sonrası yürüme geometrisi bayat kalmıyor |
| Save/reload | Tasarım `DESIGN_n` etiketlerinde, `COMMIT_FIXTURES` de öyle — multi'nin etiketleriyle kaydedilip geri yükleniyor |
| Fixture maddileştirme | Kapı/kap karoları gerçek eşya oluyor, öncekiler sökülüyor (CItemMultiCustom.cpp:344-358) |

### Kapanan boşluklar

- [x] **İŞ-35-01 (P1) — `@HouseDesignCommit` yanlış zamanda, yanlış argümanlarla.**
  Referans onu commit'ten **önce** ateşler ve şunları verir
  (CItemMultiCustom.cpp:314-327):

  | Argüman | Anlamı |
  |---|---|
  | `ARGN1` | kayıtlı (eski) tasarımın karo sayısı |
  | `ARGN2` | çalışma (yeni) tasarımın karo sayısı |
  | `ARGN3` | yeni revizyon |
  | `ARGO` | multi |
  | `LOCAL.FIXTURES.OLD/NEW` | her iki tasarımdaki fixture sayısı |
  | `LOCAL.MAXZ` | çalışma tasarımının en yüksek karo Z'si |
  | `RETURN 1` | commit'i **bırakır** |

  Bizde commit'ten **sonra**, `ARGN1 = revizyon` ve başka hiçbir şey olmadan
  ateşleniyordu. **Sonucu ölçülebilir:** referans paket inşaatı
  `(<ARGN2> - <ARGN1>) * 500` altın olarak fiyatlıyor
  (`house_typedefs.scp:608-610`). Bizim gönderdiğimizle `ARGN2 = 0` ve
  `ARGN1 = revizyon` olduğundan maliyet **negatif** çıkıyor — yani paket her
  commit'te sahibe **para ödüyordu**. Ve "bunu karşılayamazsın" reddi
  (`RETURN 1`, :611-614) hiç okunmuyordu: tasarım yine de commit ediliyordu.

  Reddedilen commit çalışma tasarımına dokunmuyor ve **oturumu açık bırakıyor**;
  referans da `CopyDesign`'dan önce dönüyor, yani oyuncu tasarım kipinde kalıp
  düzeltip yeniden deneyebiliyor.

- [x] **İŞ-35-02 (P2) — Değişmemiş tasarım yine commit sayılıyordu.**
  Referans eşit revizyonda erken döner (:277). **Uyarlama notu:** referans bunu
  revizyonla sınar çünkü **onun** çalışma tasarımının revizyonunu her düzenleme
  ilerletir; bizde revizyonu `Commit`'in kendisi artırıyor, yani dokunulmamış bir
  oturum yine de kayıtlı olanla eşit okunur. Bir şeyin değişip değişmediğini
  söyleyen şey karoların kendisi — karşılaştırma oraya taşındı.

### Kayıtlı sapma

`@HouseDesignCommitItem` (karo başına veto, `RETURN 0` o bileşeni tasarımdan
siler) yazılmadı. Referans paket onu yalnızca **yorum satırı** halinde bir prob
olarak taşıyor (`house_typedefs.scp:600-601`), canlı pakette hiç yok — İŞ-8'in
paket-tarafı kuralı gereği kayda geçti.

**Testler:** `HouseDesignCommitTriggerParityTests` (9). Yedisi `PreviewCommit`
sözleşmesini, ikisi **uçtan uca** gerçek bir `[EVENTS]` script'iyle vetoyu
sınıyor (biri `RETURN 1` ile durduruyor ve script'in gördüğü ARGN/LOCAL
değerlerini doğrulıyor, diğeri ses çıkarmayıp geçiriyor). İki düzeltme tek tek
geri alındı; her biri 2 testi kırmızıya döndürdü. Tam suite 3.557 / 0, üç arka
arkaya koşu.

**PLAN-503 kapandı.** Sıradaki: PLAN-504 (gemi yolcu/yük aktarımı, dönüş, harita
kenarı, plank, redeed).

---

## İŞ-36 — Gemi: neden durduğunu söylemek (PLAN-504, 12 Eylül 2026)

### Yöntem ve ölçüm

Referansın gemi fiil ve anahtar tabloları (`CCMultiMovable::sm_szVerbKeys`,
20 fiil; `sm_szLoadKeys`, 6 anahtar) bizimkiyle diff'lendi: **hiçbiri eksik
değil** — önceki gemi dalgaları (10AB/10CD, W243) script yüzeyini kapatmış.
Bu yüzden bu dilim davranışa baktı.

### Kapanan boşluk

- [x] **İŞ-36-01 (P2) — Duran gemi hiçbir şey söylemiyordu.**
  Referans duruş sebebini izler ve dümenden konuşturur
  (CCMultiMovable.cpp:852-860): dünyanın kenarı `DEFMSG_TILLER_TURB_WATER`,
  başka her şey `DEFMSG_TILLER_STOPPED`. **İki mesaj da bizde zaten vardı**
  (`Msg.TillerTurbWater`, `Msg.TillerStopped`) ve `TillerTurbWater` hiçbir yerde
  konuşulmuyordu — gemi sessizce duruyordu.

  Yapısal engel: harita-kenarı sınaması `CanMoveShipTo`'nun içine katlanmıştı,
  yani çağıran taraf iki sebebi ayırt edemiyordu. `IsOffMap` ayrı bir yükleme
  çıkarıldı ve `Move` artık hangi sebeple durduğunu biliyor.

### Ölçülüp DEĞİŞTİRİLMEYEN

| Ayak | Kanıt |
|---|---|
| Kısmi hareket | Engele çarpan emir geçtiği karoları korur (:841) — bizde de öyle, teste bağlandı |
| `@Ship_Move` | Yalnızca tamamlanan koşu için (:863) — aynı |
| Z tavan/taban payı | `MoveDelta` (:284-288) — zaten port edilmiş |
| Yolcu/yük seçimi | Güverte düzlemi `shipZ + max(3,height)`, -2..+16 penceresi (`ListObjs`, :142/175/203) — aynı |
| `ATTR_STATIC` | Gemiyle taşınmaz (:200) — aynı |
| Fiil/anahtar yüzeyi | 26 anahtarın tamamı karşılanıyor |

### Kayıtlı sapmalar

- **`OF_MapBoundarySailing` (0x800) tüketicisiz.** Referans bayrak açıkken gemiyi
  harita kenarında durdurmaz, **karşı kenara sardırır** (:690-732). Bayrak bizim
  `OptionFlags` enum'unda duruyor ve hiçbir şey okumuyor — İŞ-28'deki
  `OF_PETSLOTS` ile aynı sınıf. **Yine de yazılmadı** ve sebebi şu: referansın
  kaydırma aritmetiği bölge köşelerinden (`GetRegionCorner` → `GetRectCorner`)
  türetiliyor ve bizim çapa-tabanlı modelimize bire bir oturmuyor: türettiklerim
  doğu yönünde teknenin arka kenarını haritanın bir karo dışına koyuyor, yani
  sabiti doğrulayamadan portlamak gemileri geçersiz koordinata ışınlama riski.
  **Canlı shard'da bayrak kapalı** (`OPTIONFLAGS=0x2080`, 0x800 yok) ve
  kapalıyken bizim davranışımız referansla aynı. Ayrı bir iş olarak duruyor.

- **Sınır dışına düşecek nesne.** Referans `MoveDelta`'da geçersiz noktaya
  düşecek TEK nesneyi atlar ve geride bırakır (:313-317). Bizde tüm hareket
  reddediliyor. Bilinçli ve daha sıkı: referansın yolu bir yolcuyu ya da yükü
  açık denizde geminin arkasında bırakır.

**Testler:** `ShipStopReasonParityTests` (4) — iki sebebin ayrı satirlar olması,
kısmi hareketin korunması ve temiz bir koşunun sessiz kalması dahil. Düzeltme
geri alındığında 2'si kırmızıya döndü. Tam suite 3.561 / 0, üç arka arkaya koşu.

**PLAN-504 kapandı.** Sıradaki: PLAN-505 (guild üyelik/fealty/yetki modeli, stone
gump menüleri).

---

## İŞ-37 — Lonca: kimi kabul eder (PLAN-505, 12 Eylül 2026)

PLAN-505: "Üyelik/fealty/yetki modelini **önce doğrula**; stone gump menülerini bu
modelin üstüne kur. **Eksik menü ile eksik guild çekirdeğini ayır.**"

### 1. Çekirdek doğrulandı

Referansın üyelik modeli (`CStoneMember.h`) bizimkiyle karşılaştırıldı:

| Ayak | Referans | Bizde |
|---|---|---|
| Priv basamağı | CANDIDATE 0 / MEMBER 1 / MASTER 2 / ACCEPTED 4 / ENEMY 100 / ALLY 101 | aynı değerlerle `GuildPriv` |
| Fealty | `m_UIDLoyalTo` | `GuildMember.LoyalTo` |
| Unvan / kısaltma | `m_sTitle`, `m_fAbbrev` | `Title`, `ShowAbbrev` |
| Hesap altını | `m_iAccountGold` | `AccountGold` |
| Oy sayma | `m_iVoteTally` — `ElectMaster` | `ElectMaster`, fealty oyu sayar |
| Savaş/ittifak | karşılıklı `m_fWeDeclared`/`m_fTheyDeclared` | `GuildRelation`, aynı karşılıklılık |
| Ev/gemi | `MultiStorage` | `Houses`/`Ships`, `MaxHouses`/`MaxShips` |

Anahtar tabloları da diff'lendi (`CItemStone_props.tbl` 18, `_functions.tbl` 30,
`CStoneMember_props.tbl` 15): **özellik tarafında yalnızca `ISMEMBER` eksik**
(iki pakette de sıfır kullanım).

### 2. Kapanan boşluk — çekirdek

- [x] **İŞ-37-01 (P1) — Kabul kapısı hiç yoktu.**
  Referans `CItemStone::AddRecruit` iki şeyi reddeder (CItemStone.cpp:1074-1087):
  (a) oyuncu olmayanı ("Only players can be members!"), (b) **aynı türden** başka
  bir taşa bağlı olanı ("Must resign previous guild").

  Bizim `GuildDef.AddRecruit` herhangi bir Serial'ı kabul ediyordu. Sonuç:
  bir script bir NPC'yi kaydedebiliyor, ya da tek oyuncuyu aynı anda iki loncaya
  koyabiliyordu — ve `APPLYTOJOIN` yolu ona **ikinci bir taş hafızası**
  damgalıyordu, ki aidiyet/tekrar kontrolleri tam da onu okur.

  Kural tek yere kondu (`GuildManager.GetRecruitRefusal`) ve iki fiil de
  (`APPLYTOJOIN`, `JOINASMEMBER`) oradan geçiyor. Kasaba/lonca bağımsızlığı
  korundu: yalnızca aynı türden taş engelliyor. Bekleyen bir adaylık da
  engellemiyor — referans çatışmayı **üyelikten** okur.

### 3. Menüler — neden yazılmadı

Fiil tablosunda bizde karşılığı olmayan 17 giriş var: `VIEWROSTER`,
`VIEWCANDIDATES`, `VIEWCHARTER`, `VIEWENEMYS`, `VIEWTHREATS`, `MASTERMENU`,
`RETURNMAINMENU`, `ACCEPTCANDIDATE`, `REFUSECANDIDATE`, `DISMISSMEMBER`,
`GRANTTITLE`, `RECRUIT`, `DECLAREFEALTY`, `SETNAME`, `SETABBREVIATION`,
`SETCHARTER`, `SETGMTITLE`.

İlk bakışta "17 eksik fiil, yazılmalı" görünüyordu. **Referansın kendisine
bakınca öyle olmadığı çıktı:** `CItemStone::r_Verb` tablodaki 30 girişten yalnızca
13'ünü ele alıyor, gerisi `default: return false`'a düşüyor
(CItemStone.cpp:791-999). Yani bu fiiller **referansta da uygulanmıyor**;
tabloda duran ölü girişler. Onları yazmak port etmek değil **uydurmak** olurdu.

Paket ölçümü bunu destekliyor: tek çağıran
`oldSphere/Scripts-X-main/stones/guilds/legacy_guild_menu.scp` — adında
**legacy** yazan, eski motorun menü ekranlarını çağıran bir `[MENU]` script'i.
Canlı pakette kullanım **sıfır**.

**Ve canlı paketin menüye ihtiyacı yok:** `systems/stones/guilds/` altında kendi
tam lonca sistemini taşıyor — 15 diyalog (`sphere_guild_dlg_*.scp`) artı kendi
types/functions/events/speech dosyaları. Yani PLAN-505'in dediği ayırımda
**menü eksik değil** (paket kendi menüsünü getiriyor), **çekirdek eksikti**
(yukarıdaki kabul kapısı).

### Kayıtlı sapmalar

- **Red sessiz.** Referans reddi taştan konuşturur; bu motorda eşya-konuşma
  kanalı yok ve tek satır için yeni bir statik kanca eklemek orantısız.
- **`ATTR_OWNED` kasaba taşı anında üye yapar** (CItemStone.cpp:1089-1093).
  Hiçbir pakette bir taşa `attr_owned` konmuyor — kayda geçti.
- **`ISMEMBER`** üye özelliği yok; iki pakette de sıfır kullanım.

**Testler:** `GuildRecruitGateParityTests` (8) — kasaba/lonca bağımsızlığı,
bekleyen adaylığın engellememesi ve fiilin aynı kapıya sorması dahil. Kapı geri
alındığında 4'ü kırmızıya döndü. Tam suite 3.569 / 0, üç arka arkaya koşu.

**PLAN-505 kapandı.** Sıradaki: PLAN-506 (party/chat).

---

## İŞ-38 — Sohbet: gizlilik düğmeleri (PLAN-506, 12 Eylül 2026)

### Party — ölçüldü, değiştirilmedi

Anahtar tablosu (`CParty_props.tbl` 7, `CParty_functions.tbl` 10) diff'lendi:
yalnızca `SETMASTER` ve `SPEECHFILTER` karşılıksız, **ikisi de iki pakette de
sıfır kullanım** (`SPEECHFILTER`'ın tek eşleşmesi bir sözdizimi-renklendirme
tanım dosyası). Davranış tarafı da doğru çıktı:

| Ayak | Kanıt |
|---|---|
| Çıkarma yetkisi | Kendini ya da usta ise başkasını (CParty.cpp:304) — aynı kapı var |
| İki veto | `@PartyRemove` sonra `@PartyLeave`, ikisi de `RETURN 1` ile durdurur (:316-325) — ikisi de koşuyor |
| Lider ayrılınca | İstemci komutu **dağıtır** (fDisband) — `Disband` yoluna gidiyor |
| Tek kişiye düşünce | Dağılır (:359) — `PartyManager.Leave` aynısını yapar |
| `@PartyDisband` zamanı | Dağılmadan ÖNCE, kalacak üye üzerinde (:375) — aynı |
| Çevrimdışı üye | `SendMemberMsg` istemci yoksa paketi atlar (:168), `SetDisconnected` gruptan çıkarır — ikisi de böyle |
| Grup sohbeti | Alıcı grupta değilse düşer (:242) — aynı |

### Kapanan boşluk — sohbet

- [x] **İŞ-38-01 (P2) — İstemcinin yedi gizlilik eylemi hiç işlenmiyordu.**
  Referans sohbet üyesinde iki anahtar tutar, **ikisi de açık başlar**
  (`CChatChanMember.cpp:8`):

  | Anahtar | Ne yapar |
  |---|---|
  | `m_fReceiving` | Gelen özel mesajları kapatır (CChatChannel.cpp:110) |
  | `m_fAllowWhoIs` | `/whois`'in karakter adını verip vermeyeceği (CChatChannel.cpp:53) |

  İstemci bunlar için yedi ayrı eylem gönderiyor (`0x6F` +receive, `0x70`
  -receive, `0x71` /receive, `0x72` +showname, `0x73` -showname, `0x74`
  /showname, `0x75` /whois) ve `HandleChatAction`'daki switch **hiçbirini**
  tanımıyordu — yedisi de sessizce düşüyordu. Sonuç: düğmeler hiçbir işe
  yaramıyor, her oyuncu temelli ulaşılabilir ve temelli tanımlanabilir.

  **Önemli ayrım:** özel mesajı reddetmek, **yok saymak (ignore) değildir.**
  Yok sayma alıcının tek bir göndericiyi teslimde düşürmesidir; reddetme ise
  referansta mesajı **kaynağında** geri çevirir ve göndericiye söyler. Test bu
  ikisinin karışmadığını ayrıca doğrulıyor.

  İki anahtar da — yok sayma listesi gibi — oturum durumu; sohbet oturumu
  bitince unutuluyor.

### Kayıtlı sapmalar

- **`SPEECHFILTER`** yok. Referansta grup sohbetinde her mesaj için çağırılan bir
  script FUNCTION adı (ARGN1 konuşan, ARGN2 hedef, ARGS metin, `RETURN 1`
  bastırır; CParty.cpp:252-266). Hiçbir pakette kullanılmıyor.
- **`SETMASTER`** party fiili yok (içeride `PartyDef.SetMaster` var). Paketlerde
  sıfır kullanım.
- **`/whois` yanıtları düz metin.** `defmessages` anahtarı eklemek yerine, aynı
  katmandaki mevcut uygulamaya uyuldu.

**Testler:** `ChatPrivacyParityTests` (10). Özel mesaj kapısı geri alındığında 2,
yedi eylem geri alındığında 7 test kırmızıya döndü. Tam suite 3.579 / 0, üç arka
arkaya koşu.

**PLAN-506 kapandı — Dalga 5 (housing, gemi ve sosyal sistemler) tamamlandı.**
Sıradaki: Dalga 6.

