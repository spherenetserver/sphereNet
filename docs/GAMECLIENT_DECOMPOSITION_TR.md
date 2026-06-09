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
     └─ ClientViewUpdater  view-delta build/apply + known-char bildirimleri ✅
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
ViewUpdate ✅ → Inventory → ItemUse → WorldFeatures → Dialogs/Targeting →
Combat → ScriptConsole.

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

## Disiplin

- Mantık birebir taşınır; "iyileştirme" ayrı commit'tir.
- Her dilim sonrası: Release build + tam test paketi yeşil.
- Statik hook eklenirse `ResetEngineStatics`'e kaydı şarttır.
- Bir kümenin taşınamama gerekçesi (ör. doğrudan alan yazımı/dirty disiplini)
  changelog'a yazılır — sessiz erteleme yok.
