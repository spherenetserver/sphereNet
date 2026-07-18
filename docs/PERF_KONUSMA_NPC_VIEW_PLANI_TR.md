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
- [x] **S2 (P2, DÜŞÜK risk) — Speech hear-handler gate'leri YAPILDI (davranış-koruyan).** İki temiz gate:
  (1) `f_onchar_speech` global hook'u yeni `TriggerRunner.HasFunction` ile gate'lendi — fonksiyon tanımlı
  değilse (yaygın durum) her NPC her konuşma satırında TriggerArgs alloc'unu atlıyor; tanımlıysa davranış
  aynı. (2) `@NPCHearGreeting` fire'ı `IsCharTriggerUsed(NPCHearGreeting)` ile gate'lendi — hook yoksa
  dispatch+alloc atlanıyor; `MEMORY_SPEAK` kaydı KORUNUYOR (script okuyabilir). Facing + service/keyword
  dalları dokunulmadı (Human/None brain'ler train için hâlâ akıyor). Test: HasFunction_TracksRegisteredFunctions.
  Suite yeşil. NOT: kalan residual (per-NPC ToLowerInvariant tekrarı + service/keyword dispatch) S1 ve
  ileride ele alınabilir; en büyük sürücü (facing broadcast) S1'de.
- [ ] **S3 (P2) — Koşulsuz item scan'i gözden geçir.** Comm crystal yoksa/az ise scan'i gate'le.

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
- [ ] **N2 (P0 etki, YÜKSEK efor) — Read-only ağır işi paralel `BuildDecision`'a taşı.** Range-scan +
      `FindPath` map/static'e karşı read-only; paralelde çöz, somut `Move` kararı üret (Move branch
      `ApplyDecision:480-485` zaten var, sadece `MoveCharacter` mutasyonu serial kalır). En yüksek
      kaldıraç ama thread-safety kontratı dikkat ister.
- [ ] **N3 (P2) — Per-NPC pathfind tavanını düşür/batch'le** (`NpcPathMaxNodes=500`). N2 ile birlikte
      tick-latency'de önemsizleşir.

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
- [ ] **V2 (P2, ORTA risk) — OPL cache'i view-exit'te evict etme.** 30s TTL ile obje-keyed tut →
      re-entry/login burst built listeyi yeniden kullanır. Cache invalidation dikkat.
- [ ] **V3 (P3) — Redundant apply-fazı LOS'u atla** (build zaten filtreledi).
- [ ] **V4 (P3) — Burst'te tooltip defer/stagger** (draw/world hemen; `SendAosTooltip` sonraki tick'lere
      kuyrukla, ya da client hover'da `requested:true` ile zaten istiyor).

---

## Kapatma durumu (güvenlik × değer)

1. [x] **V1** — ClientTooltip gate. Commit 144a8e0.
2. [x] **N1** — NPC wake stagger. Commit d3f5a39.
3. [x] **S2** — Speech hear-hook gate (f_onchar_speech HasFunction + greeting IsTrigUsed). Commit 7649610.
4. [x] **S1** — Redundant facing broadcast kaldırıldı (dirty→view pipeline'a bırakıldı). Commit 0a8505b.
5. [ ] **V2** — OPL cache'i view-exit'te evict etme. ERTELENDİ (otonom yapılmadı): re-entry'de tooltip'i
   30s'ye kadar bayat gösterme riski = **görünür davranış değişimi**; V1 zaten baskın maliyeti (gate'siz
   trigger) çözdü, V2 ikincil. Canlı client'ta bayatlık kabul edilebilirliği doğrulanmalı.
6. [ ] **N2** — Read-only ağır işi (range-scan + A* FindPath) paralel BuildDecision'a taşı. ERTELENDİ
   (otonom yapılmadı): NPC-AI thread-safety mimarisini değiştiren **yüksek riskli** iş; build-apply arası
   world değişimi için re-validation gerektirir, canlı yük testi + gözetim ister. En yüksek değer ama
   en riskli — ayrı, gözetimli oturumda yapılmalı.

Bu dalgada V1/N1/S2/S1 (hepsi davranış-koruyan, testli, yeşil) kapatıldı. V2/N2 bilinçli olarak
gözetimli bir oturuma bırakıldı (davranış/mimari trade-off).

Her biri yapıldı: recon → en küçük fix → build + test + changelog → commit/push.
