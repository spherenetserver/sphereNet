# Performans Planı — Konuşma / NPC / View-Apply (doğrulanmış)

Kaynak: `wiki/1.txt` (log-tabanlı gözlem). Üç iddia da bağımsız recon ajanlarıyla koda karşı
**doğrulandı**. Aşağıda her iddia için: doğruluk durumu, gerçek maliyet sürücüsü, iddianın
yanlışları, ve risk-sıralı fix yüzeyi. En güvenli + yüksek-değerli işler önce kapatılacak.

Doğrulama tarihi: 2026-07-18. Bu session'ın disiplini: doğrula → en küçük güvenli kök-neden fixi.

---

## 1. Konuşma fan-out (0xAD PacketSpeechUnicode) — DOĞRU (yapısal)

Tek konuşma paketi senkron main-thread fan-out çalıştırıyor (kuyruk/deferral yok):
speaker @Speech → pet komut → **yakındaki TÜM NPC hear loop** → tüm ground item @Hear →
comm crystal → paket üretimi. `hearRange`=18 (say) sektör-pencereli.

**Gerçek maliyet sürücüleri (kanıtlı):**
- `OnNpcHearSpeech`'te **per-NPC speech-handler gate'i YOK** (`Program.NpcServices.cs:890`) →
  hiç speech handler'ı olmayan NPC bile ödüyor: 3× `ToLowerInvariant`, debug log arg-format,
  `f_onchar_speech` için `TriggerArgs` alloc, memory lookup, service-keyword scan, CharDef lookup.
- **En büyük gizli sürücü: `BroadcastFacingUpdate` per-NPC** (`Program.NpcServices.cs:906`) — her
  NPC için iç içe `ForEachClientInRange` client-scan + notoriety compute + paket → **O(NPC × yakın-client)**.
  Kalabalık bölgede süperlineer yapan bu, SPEECH parse değil.
- Item scan **koşulsuz** çalışıyor (`Program.EngineWiring.cs:718-725`, comm crystal için), SpeechEngine
  doc-yorumunun aksine (yorum "scan atlanır" diyor ama atlanmıyor).

**İddianın yanlışı:** item scan @Hear yoksa atlanmıyor (koşulsuz wired); asıl sürücü
`BroadcastFacingUpdate`, iddiada yok; `f_onchar_speech` yoksa ucuz (dict lookup).

**Fix yüzeyi (risk-sıralı):**
- [x] **S1 (davranış-koruyan çıktı) — Redundant `BroadcastFacingUpdate` kaldırıldı.** Recon: `Character.Direction`
  setter değişimde `DirtyFlag.Direction` mark ediyor; dirty→view pipeline `SendUpdateMobile` ile
  **`BroadcastFacingUpdate` ile birebir aynı `PacketMobileMoving` (0x77) + per-observer notoriety**'yi
  aynı yakın-client kümesine bir sonraki view-refresh'te (sub-100ms) gönderiyor (`ClientViewUpdater:175`
  `posChanged` Dir değişimini de yakalıyor). Yani `OnNpcHearSpeech`'teki immediate broadcast **redundant**
  bir per-NPC client-scan'di (fan-out'un en büyük maliyeti). Kaldırıldı; `npc.Direction = faceDir` korundu
  → NPC yine konuşana dönüyor, sadece teslim batched-dirty'e geçti. Davranış korunuyor (aynı paket, aynı
  client'lar). `BroadcastFacingUpdate` diğer 3 çağrı yerinde duruyor. Test: Direction_Setter_MarksDirtyOnChange
  (S1'in dayandığı invariant guard'ı). Suite yeşil.
  KALAN (S1'in orijinal "en büyük sürücü" hedefi): NPC-yoğun bölgede facing yine de dirty-pipeline'dan
  gidiyor (per-NPC SendUpdateMobile), ama artık ayrı bir senkron scan değil — normal view-refresh'e
  katıldı. Ek batch/dedupe gerekirse ayrı iş.
  NÜANS (doğrulama 2, 2026-07-18): teslim aslında dirty-flag'e bağlı DEĞİL — view pipeline her
  refresh'te `LastKnownPos` ile diff alıyor (`ClientViewUpdater:175` Dir karşılaştırması), yani
  Direction değişimi dirty flag hiç mark edilmese de `posChanged` olarak yakalanırdı. Teslim
  garantisi çift katmanlı (dirty flag + per-refresh diff); Direction_Setter_MarksDirtyOnChange
  testi yararlı bir guard ama tek taşıyıcı invariant değil. S1'i zayıflatmaz, daha da güvenli kılar.
- [x] **S2 (P2, DÜŞÜK risk) — Speech hear-handler gate'leri YAPILDI (davranış-koruyan).** İki temiz gate:
  (1) `f_onchar_speech` global hook'u yeni `TriggerRunner.HasFunction` ile gate'lendi — fonksiyon tanımlı
  değilse (yaygın durum) her NPC her konuşma satırında TriggerArgs alloc'unu atlıyor; tanımlıysa davranış
  aynı. (2) `@NPCHearGreeting` fire'ı `IsCharTriggerUsed(NPCHearGreeting)` ile gate'lendi — hook yoksa
  dispatch+alloc atlanıyor; `MEMORY_SPEAK` kaydı KORUNUYOR (script okuyabilir). Facing + service/keyword
  dalları dokunulmadı (Human/None brain'ler train için hâlâ akıyor). Test: HasFunction_TracksRegisteredFunctions.
  Suite yeşil. NOT: kalan residual (per-NPC ToLowerInvariant tekrarı + service/keyword dispatch) S1 ve
  ileride ele alınabilir; en büyük sürücü (facing broadcast) S1'de.
- [x] **S3 (P2) — Item scan sektör listen-item sayacıyla gate'lendi. YAPILDI (dalga 2).**
  Source-X modeli birebir: `CSector::m_ListenItems` (CClientEvent.cpp:1883 `HasListenItems()`
  gate'i) → SphereNet'te `Sector._listenItems` sayacı (CommCrystal + Multi/MultiCustom/Ship;
  Source-X sadece crystal sayar ama bizde multi/gemi konuşması da aynı scan'den geçiyor).
  Sayaç bakımı: `Sector.AddItem/RemoveItem` + ground item retype hook'u (`Item.ItemType` setter →
  `GameWorld.NotifyGroundItemTypeChanged`). Konuşma yolu: `SpeechEngine` scan'i
  `HasListenItemsInRange` VEYA `ScanAllItemsOnHear` (herhangi bir item def @Hear script'liyorsa)
  açıksa çalıştırıyor. Test: ListenItemSpeechGateTests (4). Commit b4420d7.

---

## 2. NPC AI seri commit — DOĞRU (iddiadan da kötü)

`RunMulticoreTick` (`Program.Tick.cs:627`): paralel faz `npc_build` (6.4ms) + seri faz `npc_apply`/
`commit` (106ms/140 karar). **Paralel faz gerçekte hiç AI işi yapmıyor.**

**Kanıt:**
- `BuildDecision` (`NpcAI.cs:443-467`) uyanık her NPC için **koşulsuz `Legacy` placeholder** döndürüyor
  (`:466`); `Move` kararı hiç üretilmiyor (branch var ama ölü). Tüm ağır beyin serial
  `ApplyDecision → OnTickAction` (`NpcAI.cs:489`)'da.
- Seri maliyet: range-scan'ler (`NpcAI.Combat.cs:189` vb.) + **A\* `FindPath`** (`NpcAI.Movement.cs:512`,
  500-node bütçe, ulaşılamaz hedefte ~17ms/NPC) + `MoveCharacter` + trigger'lar.
- Login spike: 5×5 pencere (`ActiveSectorRadius=2`) ~140 NPC uyandırıp `WakeNpc`'de hepsini
  **`now+100`'e (aynı wheel slot)** schedule ediyor (`Program.Tick.cs:844`) → tek tick grubu, stagger yok.
  `MaxNpcsPerTick=500` bütçesi 140'ta devreye girmiyor.

**İddianın yanlışı:** "çoğu NPC" değil **tüm** uyanık NPC; dirty-flush maliyet DEĞİL (sub-ms tail);
500-bütçe 140'ı ne yaratıyor ne yumuşatıyor.

**Fix yüzeyi (risk-sıralı):**
- [x] **N1 (P2, DÜŞÜK risk) — NPC wake stagger YAPILDI.** `WakeNewlyActiveSectorNpcs` artık her NPC'yi
      `now+100 + (uid % NpcWakeSpreadMs)` (spread=800ms = 8 slot) ile schedule ediyor → ~140 NPC tek
      wheel slotu yerine ~8 slota dağılıyor. Stagger sadece TOPLU sector-activation yolunda; tekil
      aggro/interaction wake'leri (`WakeNpc(target)`) anında kalıyor (iki overload, 1-arg olan
      `Action<Character>` delegesine bağlanıyor). Offset uid-türevli (RNG yok → tick determinizmi korunur).
      Test: TimerWheel_StaggeredSchedule_SpreadsAcrossSlots. Suite yeşil.
- [x] **N2 (P0 etki) — Pathfind prestage paralel `BuildDecision`'da. YAPILDI (dalga 2, daraltılmış
      kapsam).** Bloklu combat/pet chase A*'ı artık paralel build fazında koşuyor (`Pathfinder`
      scratch state'i zaten `[ThreadStatic]` — altyapı hazırmış) ve sonuç `NpcDecision` üzerinde
      taşınıyor (`PrestagedPath/PrestageGoal/PrestageRan`). Serial apply, KENDİ durumu hâlâ
      recompute gerektiriyorsa seed'liyor (taze cache/throttle varsa prestage çöpe gider — serial
      otorite kalır) ve her adım `CanNpcMoveTo` ile yeniden doğrulanıyor. KAPSAM NOTU: plan
      "somut Move kararı üret" diyordu; brain semantiği (trigger'lar, cadence, RNG) serialde
      kalmalı diye cache-seed modeli seçildi — aynı kaldıraç, sıfır davranış riski. Acquire
      range-scan'leri serialde (trigger fire edebiliyorlar). Test: NpcPathPrestageTests (3).
      Commit 82960c0.
- [x] **N3 (P2) — Pathfind maliyeti sınırlandı. YAPILDI (dalga 2, kapsam güncellendi).** Doğrulama:
      negatif cache zaten vardı (`PathFailBackoffMs=5000` + `PathThrottleMs=750`) — plan bayattı.
      Gerçek eksikler Source-X parity gate'leriydi: (1) `NPC_Pathfinding` mesafe uygunluğu
      (CCharNPCAct.cpp:2432/:2434) — A* yalnızca 2..13 tile hedefe; (2) 28×28 arama kutusu
      (`MAX_NPC_PATH_STORAGE_SIZE`) → `Pathfinder.FindPath(maxRadius:14)` — kapalı alanda
      unreachable hedef kutuyu tüketir, 500-node bütçeyi değil; (3) BONUS KÖK NEDEN: hedef
      karakterin kendi tile'ı A*'da hard-block sayılıyordu → BİR KARAKTERE DOĞRU HER chase
      pathfind'i "unreachable" olup tüm bütçeyi yakıyordu (planın ölçtüğü 17ms burn'ün ana
      kaynağı). Source-X mobil push-through'una uygun olarak yalnız GOAL tile'ında char blokajı
      yok sayılıyor; rotalar artık gerçekten bulunuyor. `NpcPathMaxNodes=500` korundu.
      Test: PathfinderTests.FindPath_MaxRadius_ConfinesSearchBox. Commit 82960c0.

---

## 3. View-apply / tooltip (~50ms) — DOĞRU (çekirdek tez)

`ClientViewUpdater.ApplyViewDelta` (`:148`): `view_build` paralel/ucuz (0.1ms), `apply` seri (50ms).
Maliyet arama değil, **objeleri client'a uygulama** — özellikle per-yeni-obje AOS tooltip.

**Sıralı gerçek maliyet:**
1. **Gate'siz tooltip script-trigger'ı** (`ClientSkillsHandler.cs:654-670` + `:733`): her yeni obje için
   `CharTrigger.ClientTooltip`/`ItemTrigger.ClientTooltip` + `ClientTooltipAfterDefault` **`IsTrigUsed`
   gate'i OLMADAN** fire ediliyor. Handler yoksa bile: 2× observer/target event snapshot (`.ToArray()`) +
   **CHARDEF/ITEMDEF section re-parse** (`TriggerRunner.cs:144-158`) + `TriggerArgs`/scope alloc'ları.
   **En büyük israf.** (Karşılaştır: single-click yolu `ClientInventoryHandler.cs:203/214`'te gate'li.)
2. **Tooltip sıfırdan rebuild + tam OPL serileştirme** (`ClientSkillsHandler.cs:629-768`): 30s cache var
   (`ToolTipCache`) AMA obje view'dan çıkınca evict ediliyor (`ClientViewUpdater.cs:263-264/293-294`) →
   yeni-giriş yolunda cache her zaman soğuk → tam rebuild + full `PacketOPLData` her seferinde.
3. **Per-obje LOS raycast** (`CanSendAosTooltip` → `CanSeeLOS`, `:782/:801`) — build fazı zaten görünürlük
   filtreledi, apply'da redundant.

**İddianın yanlışı:** statik-kapı senkronu per-obje DEĞİL (per-client-per-tick, global açık-kapı seti
üzerinde); tooltip cache "yok" değil — 30s var ama exit-evict onu burst yolunda etkisiz kılıyor.

**Fix yüzeyi (risk-sıralı):**
- [x] **V1 (P1, ÇOK DÜŞÜK risk, EN YÜKSEK değer) — ClientTooltip trigger fire'ları gate'lendi.** (YAPILDI:
      `SendAosTooltip` (`ClientSkillsHandler.cs:654-670` + `:733`) artık `@ClientTooltip` /
      `@ClientTooltipAfterDefault` fire'larını `IsCharTriggerUsed`/`IsItemTriggerUsed` ile gate'liyor —
      single-click yolunun (`ClientInventoryHandler.cs:203/214`) zaten yaptığı gibi. Switch'te `when` guard;
      trigger unused'sa CHARDEF/ITEMDEF section re-parse + alloc'lar tamamen atlanıyor. Handler yoksa fire
      script property ekleyemeyeceği için davranış birebir korunuyor. Test: TooltipTriggerGateTests (3 —
      char/item ClientTooltip + AfterDefault + script [ON=@ClientTooltip] BuildUsedTriggerCache). Suite yeşil.)
- [x] **V2 (P2) — OPL cache obje-keyed yapıldı, view-exit evict kaldırıldı. YAPILDI (dalga 2).**
      Source-X modeli birebir (`CObjBase::SetPropertyList`, CClientMsg_AOSTooltip.cpp:170-173;
      eviction SADECE zaman-tabanlı, `hasExpired(TOOLTIPCACHE)`): built OPL artık
      `ObjBase.TooltipCache`'te — tek build tüm gözlemcilere, view-exit'te düşmez, objeyle ölür.
      Per-client `TooltipDataCache` tamamen kalktı (sweep/prune gerekmez). Invalidate önceki
      entry'yi koruyarak rebuild zorlar → hash değiştiyse revizyon artar (null'lamak revizyonu
      1'e sıfırlayıp client'ta bayat tooltip bırakıyordu — testle yakalandı). Test:
      TooltipObjectCacheTests (2). Commit 7c2febd.
- [x] **V3 (P3) — Tooltip LOS raycast'i kaldırıldı. YAPILDI (dalga 2).** Recon düzeltmesi: LOS
      "build zaten filtreledi diye redundant" DEĞİLDİ — build LOS filtrelemiyor; asıl gerçek,
      Source-X'in tooltip yollarında LOS raycast'in HİÇ olmaması (addAOSTooltip mesafe-only,
      CClientMsg_AOSTooltip.cpp:55; 0xD6 handler'ı CanSee, receive.cpp:3628). Yani hem perf hem
      davranış fix'i: client duvar arkasındaki objeyi çiziyordu ama tooltip'i sessizce gelmiyordu.
      Test: TooltipLosGateTests. Commit f0d6bce.
- [x] **V4 (P3) — Burst tooltip'leri version-only push'a çevrildi. YAPILDI (dalga 2, plandan sapma).**
      Kuyruk/stagger İCAT EDİLMEDİ — Source-X'in kendisi bunu `TOOLTIPMODE_SENDVERSION` (default;
      CClientMsg_AOSTooltip.cpp:176-207) ile çözüyor: view'a giren obje için sadece 0xDC revizyon
      paketi gider, full 0xD6'yı client kendi cache miss'inde ister (doğal client-pull stagger).
      Full push yalnız: requested, build'de hash değişti (stale-UID guard, :148-157), veya mode 2.
      Per-client sent-hash defteri (`TooltipHashCache`) tamamen kalktı — yerini build-anı
      `hashChanged` bayrağı aldı. Commit 533710f.

---

## Kapatma durumu (güvenlik × değer)

1. [x] **V1** — ClientTooltip gate. Commit 144a8e0.
2. [x] **N1** — NPC wake stagger. Commit d3f5a39.
3. [x] **S2** — Speech hear-hook gate (f_onchar_speech HasFunction + greeting IsTrigUsed). Commit 7649610.
4. [x] **S1** — Redundant facing broadcast kaldırıldı (dirty→view pipeline'a bırakıldı). Commit 0a8505b.
5. [x] **V3** — Tooltip LOS raycast kaldırıldı (Source-X mesafe+CanSee). Commit f0d6bce.
6. [x] **S3** — Item-hear scan sektör listen-item sayacıyla gate'lendi. Commit b4420d7.
7. [x] **V2** — OPL cache obje-keyed + sadece TTL eviction. Commit 7c2febd. (Önceki erteleme
   gerekçesi olan "30s bayatlık" Source-X'in TASARIMI çıktı — TOOLTIPCACHE saf zaman-tabanlı,
   view-exit eviction referansta yok; parity gereği kabul edildi, invalidate yolu revizyon
   artırarak değişiklikleri anında duyuruyor.)
8. [x] **V4** — Version-only tooltip push (Source-X SENDVERSION); kuyruk yerine client-pull. Commit 533710f.
9. [x] **N3** — A* mesafe gate'leri (2..13) + 28×28 arama kutusu + goal-tile char blokajı fix'i
   (chase A*'ını "her zaman unreachable" yapan kök neden). Commit 82960c0.
10. [x] **N2** — Bloklu chase A*'ı paralel BuildDecision'da prestage; serial otorite + adım
    re-validation korunarak. Commit 82960c0.

Dalga 1 (V1/N1/S2/S1) + dalga 2 (V3/S3/V2/V4/N3/N2) ile plan TAMAMEN kapandı. Her madde:
recon (Source-X'e karşı, 3 paralel ajan) → en küçük kök-neden fixi → build + test (tam suite
yeşil: 1865/1868, 3 skip) → çift changelog → commit. Dalga 2'nin iki plan-düzeltmesi:
N3'ün "negatif cache ekle" tarifi bayattı (zaten vardı; gerçek eksik mesafe gate'leri ve
goal-tile bug'ıydı) ve V4'ün "kuyruk/stagger" tarifi gereksizdi (Source-X SENDVERSION zaten
doğal stagger).
