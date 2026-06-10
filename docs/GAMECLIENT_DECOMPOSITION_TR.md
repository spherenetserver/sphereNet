# GameClient Ayrıştırma Planı

GameClient ~16.000 satır, 17 partial dosya: paket handler'ları, görünüm
güncellemeleri, script konsolu ve tüm motor köprüleri tek mantıksal sınıfta.
Partial dosyalar fiziksel bölme sağlıyor ama sorumluluk ayrımı sağlamıyor —
her partial diğerlerinin private alanlarına serbestçe erişiyor.

Bu plan, Character ayrıştırmasında kanıtlanan **birebir taşıma** disipliniyle
(mantık satır satır taşınır, API delegasyonla korunur, her dilim sonrası tam
test koşusu) ilerler.

## Hedef mimari

```
GameClient (orkestratör: NetState + Character + yaşam döngüsü)
 ├─ ClientTargetState      hedef-cursor durum makinesi (faz 1 ✅)
 ├─ ClientGumpRegistry     açık gump/dialog kayıtları (faz 1 ✅)
 ├─ ClientMovementThrottle walk-token / hareket kuyruğu (faz 1 ✅)
 ├─ ClientViewCache        known-set'ler + last-known durumlar (faz 2 ✅)
 └─ handler sınıfları      (faz 3 — sürüyor)
     ├─ ClientViewUpdater       view-delta build/apply + known-char bildirimleri ✅
     ├─ ClientInventoryHandler  single click, pickup/drop/equip, profil/status ✅
     ├─ ClientItemUseHandler    dclick dispatch, item kullanımı, pet komutları, vendor listeleri ✅
     ├─ ClientWorldFeaturesHandler  crafting/guild/house gump, trade, 0xBF dispatch, party, context menu ✅
     ├─ ClientTargetingHandler  target-response yönlendirici, gump gönderim/yanıt ✅
     ├─ ClientDialogHandler     script DIALOG render, named-dialog dispatch, help menü, INPDLG ✅
     ├─ ClientSkillsHandler     info/aktif skill akışları, tooltip, obje mesajı ✅
     └─ ClientCombatHandler     hareket doğrulama, konuşma, swing/ölüm/diriliş, büyü, tick pompası ✅
```

## Fazlar

**Faz 1 — durum makineleri (bu dalga).** GameClient'taki kendi-içinde-tutarlı
durum kümeleri ayrı sınıflara çıkar; partial'lardaki referanslar bileşen
üyelerine yeniden adlandırılır. Davranış değişmez; bileşenler faz 3'teki
handler sınıflarının enjekte edilebilir bağımlılıkları olur.

**Faz 2 — görünüm önbelleği (✅).** `KnownChars`, `KnownItems`,
`KnownDoorOverrides`, `LastKnownPos`, `LastKnownItemState`,
`TooltipHashCache` → `ClientViewCache` (`GameClient.View`). ViewUpdate
partial'ının tek gerçek durumu artık dışarıda; ViewUpdate faz 3'te ilk
dönüştürülecek handler'dır.

**Faz 3 — handler sınıfları (sürüyor).** Her partial, GameClient'ı context
alan bir sınıfa dönüşür (ileride dar bir `ClientContext` arayüzüne
indirgenebilir). Partial'lar arası private erişim bu noktada derleyici
tarafından zorlanan gerçek sınırlara dönüşür: handler yalnızca GameClient'ın
internal/public yüzeyini görebilir. Sıra (en az iç bağımlılıdan en çoğa):
ViewUpdate ✅ → Inventory ✅ → ItemUse ✅ → WorldFeatures ✅ → Dialogs/Targeting →
Combat → ScriptConsole.

**Inventory dönüşüm notu — "context shim" deseni:** Gövdeyi bayt-bayt korumak
için handler, GameClient yüzeyini private shim üyelerle aynalar
(`private Character? _character => _client.Character;`,
`private void SysMessage(...) => _client.SysMessage(...);` vb.). Shim bloğu,
handler'ın gerçek bağımlılık listesinin kendisidir — ileride `ClientContext`
arayüzü tam bu listeden türetilir. `ScriptConsole = this` geçişleri
`_client`'a çevrilir (konsol kimliği GameClient'tır). Derleyici eksik
bağımlılıkları sıralar; her biri ya shim ya internal yükseltmeyle kapanır.

**ViewUpdate dönüşüm notları (sonrakiler için şablon):**
- `ClientViewUpdater(GameClient)`; eski partial yalnızca delegasyon tutar —
  hiçbir çağrı noktası değişmez.
- Handler'ın ihtiyaç duyduğu çapraz-partial private üyeler `internal`'a
  yükseltilir (burada: 8 `Send*` paket yardımcısı + `World` erişimi).
- `_character` null-kontrol deseni `var me = _client.Character;` yerel
  değişkenine çevrilir; mantık birebir kalır.
- Dışarıdan referans alan iç tipler namespace seviyesine çıkar
  (`ClientViewDelta` — Server'daki delta havuzları kullanıyor).

**Kapsam dışı / dikkat:** `ITextConsole` implementasyonu ve script-konsol
yüzeyi GameClient üzerinde kalır (script'ler konsol olarak GameClient
referansı alır). Login/karakter-seçim akışı NetState yaşam döngüsüne bağlı —
en son ele alınır.

## Kalan iş planı (oturum devri — 2026-06-10 itibarıyla)

Tamamlanan: faz 1-2 (durum bileşenleri), faz 3a-d (ViewUpdate, Inventory,
ItemUse, WorldFeatures). Test tabanı: 723 yeşil. Güncel partial boyutları ve
dönüşüm sırası:

| Sıra | Partial | Satır | Hedef sınıf | Notlar / riskler |
|------|---------|-------|-------------|------------------|
| 3d ✅ | WorldFeatures | ~1583 | ClientWorldFeaturesHandler | Statik 0xBF sözlüğü `Action<ClientWorldFeaturesHandler, byte[]>` tipine döndü — lambda'lar bayt-bayt aynı (`client.X` artık handler üyesi). 6 trade callback property + test reflection köprüleri (SendContextMenu, HandleContextMenuResponse) GameClient'ta. |
| 3e ✅ | Targeting | ~627 | ClientTargetingHandler | SendGump/SetPendingTarget GameClient'ta delegasyon. Dialog close-fn durumu (`_pendingDialogCloseFunction`/`_pendingDialogArgs`) GameClient'ta internal property köprüsüyle — alanlar 3e Dialogs'ta handler'a taşınacak. |
| 3e ✅ | Dialogs | ~1629 | ClientDialogHandler | `_dialogSubjectUid`/`_nativeDialogFallbacks`/`_pendingInputDlg`/`_nextInputDlgContext` alanları + `OpenNamedDialog`/`RegisterNativeDialogFallbacks` GameClient.cs'ten handler'a taşındı. ScriptConsole `Dialogs.DialogSubjectUid`, Handlers `Dialogs.PendingInputDlg` üzerinden erişir. `TryFindMenuSection`/`IsPlainDefToken` internal köprü. |
| 3f ✅ | Skills | ~489 | ClientSkillsHandler | GameClient property adı `SkillUse` (SkillH motor erişimcisi ve `Skills` namespace'iyle çakışmamak için). `InfoSkillSink` GameClient'ta nested kaldı — ItemUse handler ve motorlar `GameClient.InfoSkillSink` olarak kuruyor. |
| 3f ✅ | Combat | ~1790 | ClientCombatHandler | Statik hareket/speed-hack config yüzeyi + OnSpeedHackDetected event'i GameClient'ta kaldı (ResetEngineStatics disiplini); handler statik shim'lerle okur, event `RaiseSpeedHackDetected` köprüsüyle ateşlenir. Throttle bileşeni + hareket durumu handler'a taşındı; vitals alanları (`_lastHits` vb.) Login da yazdığı için GameClient'ta internal kaldı. RuntimePerformancePressureTests yeşil (6/6). |
| 3g | ScriptConsole | ~2024 | ClientScriptConsoleHandler | EN BÜYÜK. ITextConsole implementasyonu GameClient'ta KALIR (script'ler konsol olarak GameClient alır); handler script-verb yüzeyini barındırır, SysMessage/GetName GameClient'ta. `TryExecuteScriptCommand`/`TryGetScriptVariable` public delegasyon. |
| — | PacketHelpers | ~1470 | (dönüştürülmez) | Paket primitifleri: GameClient'ın internal gönderim yüzeyi olarak kalır; handler'ların ortak bağımlılığıdır. İstenirse ileride `ClientPacketSender` bileşeni. |
| — | Login / Handlers / Chat / Housing | ~693/460/117/172 | (şimdilik kalır) | Login NetState yaşam döngüsüne bağlı (en son). Handlers (kitap/prompt) + Chat + Housing küçük ve zaten dar yüzeyli — 3g sonrası değerlendirilir. |

**Faz 4 (3g sonrası):** Handler'lardaki shim blokları birleştirilip dar bir
`ClientContext` arayüzü türetilir; GameClient orkestratöre iner.

**Reçete (her dönüşümde):**
1. Boyut + public yüzey + çapraz-partial INBOUND çağrı taraması (private'lara).
2. Test reflection taraması (`GetField/GetMethod` adları).
3. Gövdeyi bayt-bayt kopyala (explicit UTF-8! `tools/scan-mojibake.ps1` ile doğrula).
4. Eski partial → public delegasyonlar + inbound köprüler (internal).
5. Derleyici-güdümlü shim/internal kapatma (`ScriptConsole = this` → `_client`).
6. Tam test (723+); doküman + changelog; commit.

**GameClient dışı kalan iş:** Character stat bloğu (vitals regen + hunger +
çekirdek stat alanları) — önce davranışsal test ağı örülmeli (clamp/dirty
semantiği), sonra extraction; gerekçe Wave 83 changelog'unda.

## Disiplin

- Mantık birebir taşınır; "iyileştirme" ayrı commit'tir.
- Her dilim sonrası: Release build + tam test paketi yeşil.
- Statik hook eklenirse `ResetEngineStatics`'e kaydı şarttır.
- Bir kümenin taşınamama gerekçesi (ör. doğrudan alan yazımı/dirty disiplini)
  changelog'a yazılır — sessiz erteleme yok.
