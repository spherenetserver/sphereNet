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
 ├─ ClientViewCache        _knownChars/_knownItems/_lastKnown* (faz 2)
 └─ handler sınıfları      ClientContext üzerinden (faz 3)
```

## Fazlar

**Faz 1 — durum makineleri (bu dalga).** GameClient'taki kendi-içinde-tutarlı
durum kümeleri ayrı sınıflara çıkar; partial'lardaki referanslar bileşen
üyelerine yeniden adlandırılır. Davranış değişmez; bileşenler faz 3'teki
handler sınıflarının enjekte edilebilir bağımlılıkları olur.

**Faz 2 — görünüm önbelleği.** `_knownChars`, `_knownItems`,
`_lastKnownPos`, `_lastKnownItemState`, `_tooltipHashCache` →
`ClientViewCache`. ViewUpdate partial'ının tek gerçek durumudur; çıkınca
ViewUpdate faz 3'te ilk dönüştürülecek handler olur.

**Faz 3 — handler sınıfları.** Her partial, `ClientContext`'e (NetState,
Character, World, motorlar, Send/SysMessage/BroadcastNearby ve faz 1-2
bileşenleri) bağımlı bir sınıfa dönüşür. Partial'lar arası private erişim bu
noktada derleyici tarafından zorlanan gerçek sınırlara dönüşür. Sıra önerisi
(en az iç bağımlılıdan en çoğa): ViewUpdate → Inventory → ItemUse →
WorldFeatures → Dialogs/Targeting → Combat → ScriptConsole.

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
