# SphereNet Genel Proje İncelemesi ve Uygulama Planı

## Genel sonuç

Projenin çekirdeği oldukça kapsamlı ve genel olarak sağlam görünüyor. Tam test paketi **1.807 test başarılı, 0 hatalı, 0 atlanan** sonucuyla tamamlandı. Buna rağmen özellikle başlangıç sırasından kaynaklanan, testlerin yakalayamadığı birkaç gerçek bağlantı problemi var.

İncelemede doğrudan sunucuyu sürekli çökerten bir P0 bulamadım. En önemli riskler:

- Bazı motorlar oluşturulmadan önce birbirine bağlanmaya çalışılıyor.
- Bazı komutlar tanımlı olmasına rağmen çalışma zamanında hiçbir işlem yapmıyor.
- Çok sayıda ayar okunuyor fakat oyuna uygulanmıyor.
- Normal kapanışta dünya otomatik kaydedilmiyor.
- Bazı mekaniklerin yalnızca ilk katmanı var; yaşam döngüleri tamamlanmamış.

İnceleme sırasında herhangi bir kaynak dosyada değişiklik veya kod yazımı yapılmadı.

## Kesin bağlantı problemleri

| Öncelik | Problem | Olası etkisi |
|---|---|---|
| P1 | Hareket motoruna `SpellEngine`, motor henüz oluşturulmadan atanıyor. [Atama](../src/SphereNet.Server/Program.EngineWiring.cs#L472), gerçek [oluşturma](../src/SphereNet.Server/Program.EngineWiring.cs#L1195) daha sonra yapılıyor. | Hareket ederek büyü bozma desteği kodda var, fakat gerçek sunucuda bu bağlantı `null` kalıyor. [MovementEngine kullanımı](../src/SphereNet.Game/Movement/MovementEngine.cs#L150) |
| P1 | NPC AI gece ışığını, `WeatherEngine` oluşturulmadan önce bağlamaya çalışıyor. [Bağlantı](../src/SphereNet.Server/Program.EngineWiring.cs#L1716), [WeatherEngine oluşturma](../src/SphereNet.Server/Program.EngineWiring.cs#L2039) | Ek AI modundaki NPC’ler sürekli gündüz olduğunu varsayıyor; gece ışık yakma/söndürme davranışı çalışmıyor. [NpcAI varsayılanı](../src/SphereNet.Game/AI/NpcAI.cs#L310) |
| P1 | `.SHUTDOWN` ve `.BROADCAST` komutları tanımlı ancak sunucu tarafında subscriber yok. Yalnızca save bağlanmış. [Komut olayları](../src/SphereNet.Game/Speech/SpeechEngine.cs#L268), [yalnız SAVE bağlantısı](../src/SphereNet.Server/Program.EngineWiring.cs#L3104) | Komut kabul edilmiş gibi görünerek sessizce hiçbir şey yapmıyor. Operasyon sırasında oldukça yanıltıcı. |
| P1 | Normal shutdown sırasında kayıt bilinçli olarak yapılmıyor. [Program.Tick.cs](../src/SphereNet.Server/Program.Tick.cs#L261) | Planlı kapatmada son periyodik save’den sonraki değişiklikler kaybolabilir. |
| P2 | `TICKPERIOD`, `ServerTickMs` ve dokümandaki tick hızı birbiriyle uyuşmuyor. [sphere.ini](../config/sphere.ini#L517), [README](../README-TR.md#L145) | Yönetici 250 ms ayarladığını sanabilir; sunucu fiilen 100 ms varsayılanıyla çalışabilir. Mekanik süreleri ve performans hesabı belirsizleşiyor. |

Bu sorunların ortak sebebi, başlangıç kodunun çok uzun ve sıralamaya hassas olması. Motorlar tek tek test edildiği için işlevleri geçiyor; gerçek sunucu başlangıcındaki nesne grafiği doğrulanmıyor.

## Okunan fakat oyuna uygulanmayan önemli ayarlar

Burada iki farklı durum var: bazı seçeneklerin altyapısı gerçekten hazır ama bağlantısı yok; bazıları ise eski Sphere uyumluluğu için yalnızca parse ediliyor.

Öncelikli olanlar:

- `GameMinuteLength`: Okunuyor, fakat oyun saati hâlâ sabit 20 saniye kullanıyor. [GameWorld](../src/SphereNet.Game/World/GameWorld.cs#L1061) Mevcut `sphere.ini` içindeki değer 8 saniye ve uygulanmıyor.
- `SectorSleep`: Sector uyutma sistemi ve testleri var, fakat süre sabit 10 dakika. [Sector.cs](../src/SphereNet.Game/World/Sectors/Sector.cs#L38)
- `DistanceWhisper`, `DistanceTalk`, `DistanceYell`: Ayarlar parse ediliyor ancak konuşma motorunda mesafeler sabit. [SpeechEngine.cs](../src/SphereNet.Game/Speech/SpeechEngine.cs#L46)
- `MapViewSize` ve `MapViewSizeMax`: Ağ katmanında görüntü mesafesi sabit 18, sınırlar 5–24. [NetState.cs](../src/SphereNet.Network/State/NetState.cs#L804)
- `MaxFame`, `MaxKarma`, `MinKarma`: Ölüm motoru sabit limitlerle clamp ediyor. [DeathEngine.cs](../src/SphereNet.Game/Death/DeathEngine.cs#L459)
- `MinCharDeleteTime`: Okunmasına rağmen karakter, hesap doğrulamasından sonra hemen silinebiliyor. [GameClient.Handlers.cs](../src/SphereNet.Game/Clients/GameClient.Handlers.cs#L62)
- `DeadCannotSeeLiving`: Ayar mevcut ancak görünürlük güncelleyicisinde bu kurala karşılık gelen filtre bulunmuyor.
- `UseHttp`: Ayar okunuyor fakat web status servisi koşulsuz kuruluyor. [Program.AdminPanel.cs](../src/SphereNet.Server/Program.AdminPanel.cs#L175)
- `MapReadId`: Map tanımında var, fakat dünya ve map data başlatılırken `MapSendId` kullanılıyor. [Program.cs](../src/SphereNet.Server/Program.cs#L713)
- Map `SectorSize`: Parse ve validation var fakat çalışma zamanındaki sector boyutu sabit.
- `SaveBackground`, `SaveSectorsPerTick`, `SaveStepMaxComplexity`: Incremental/background save ayarları bulunuyor fakat save hâlâ ana döngüde senkron çalışıyor.
- `ConnectingMax`, `DeadSocketTime`, `FreezeRestartTime`, `NetworkThreads` ve `NotoTimeout`: Çalışma zamanı tüketicileri bulunmuyor.

MySQL’in eski tekil ayarları bu listeye dahil edilmemeli; bunlar yeni bağlantı tanımlarına dönüştürülen uyumluluk alias’ları gibi görünüyor.

## Kısmen tamamlanmış oyun mekanikleri

### Tarım sistemi

Tohum, ekim ve hasat temeli var; ancak gerçek bir büyüme döngüsü yok:

- Tohum ekildiğinde doğrudan son `Crops` nesnesi oluşturuluyor. [PlantSeed](../src/SphereNet.Game/Clients/ClientItemUseHandler.cs#L1671)
- Hasattan sonra yalnızca 60 saniyelik `REAP_TIME` atanıyor. [HarvestPlant](../src/SphereNet.Game/Clients/ClientItemUseHandler.cs#L1596)
- `Item.OnTick` tarafında tohum → filiz → yetişkin → ürün aşamaları bulunmuyor.

Sonuç olarak tarım çalışıyor gibi görünse de büyüme, görsel evre, sulama/toprak ve kalıcı yaşam döngüsü henüz yok.

### Işık kaynakları

Meşale/ışık açıldığında bir `LIGHT_CHARGES` eksiliyor. [ClientItemUseHandler.cs](../src/SphereNet.Game/Clients/ClientItemUseHandler.cs#L1227)

Ancak:

- Yanma süresi yok.
- Zamanla charge tüketimi yok.
- Otomatik sönme yok.
- Save/load sonrasında devam edecek mutlak yanma zamanı yok.

NPC ışık kullanımı da aynı tek-seferlik charge modelini kullanıyor.

### Büyü okulları

Enum ve spellbook altyapısı Magery, Necromancy, Chivalry, Bushido, Ninjitsu, Spellweaving ve Mysticism alanlarını kapsıyor. [SpellTypes.cs](../src/SphereNet.Core/Enums/SpellTypes.cs#L6)

Fakat özel mekanik uygulamaları ağırlıklı olarak Magery ve Necromancy’ye odaklanıyor. [ApplySpecificSpell](../src/SphereNet.Game/Magic/SpellEngine.cs#L1628) Diğer okullardaki bazı büyüler genel damage/heal/target altyapısından yararlanabilir; ancak okulun kendine özgü davranışının var olduğu varsayılamaz.

Buradaki risk, eksik büyünün açıkça “desteklenmiyor” demek yerine mana/reagent tüketip etkisiz veya yalnızca genel bir sonuç üretmesi.

## Hazırlanmış fakat kullanılmayan altyapılar

- `BotPerformanceGate` mevcut ancak bot senaryo raporunun sonucuna veya CI çıkış koduna bağlı değil. [BotPerformanceGate.cs](../src/SphereNet.Game/Diagnostics/BotPerformanceGate.cs#L5)
- Fast-walk altı anahtar stack paketleri yazılmış fakat gerçek bağlantıda gönderilmiyor. Yalnızca paket oluşturma testi var. [ExtendedPackets.cs](../src/SphereNet.Network/Packets/Outgoing/ExtendedPackets.cs#L1344)
- `ExpansionInfo` ayrıntılı expansion/feature tablosu içeriyor fakat startup, özellik maskelerini başka bir yoldan kuruyor. [ExpansionInfo.cs](../src/SphereNet.Core/Configuration/ExpansionInfo.cs#L5)
- `ExpressionGlobals` ve `ConditionalEvaluator` için aktif tüketici bulunmuyor. Bunlar muhtemelen eski ya da alternatif scripting mimarisinin artıkları; ikinci bir global durum sistemi olarak bağlanmaları doğru olmaz.
- `GameClient.OnSpeedHackDetected` dış olayının tüketicisi yok; ancak mevcut verdict/kick yolu çalıştığı için bu doğrudan güvenlik açığı değil, kullanılmayan bir audit hook.

## Test ve dokümantasyon güven boşlukları

Test sonucu güçlü olsa da aşağıdaki güvenceyi vermiyor:

- Gerçek sunucu başlangıç sırasını kuran composition root için entegrasyon testi yok.
- Testler birçok motoru `Program.EngineWiring` davranışını elle taklit ederek kuruyor. Bu nedenle motor testleri geçerken gerçek startup bağlantısı bozuk kalabiliyor.
- Üç adet gerçek map diagnostic testi, `C:\mortechUO\mul` bulunmadığında gerçek test skip’i vermek yerine `return` ediyor. [StairThrowDiagnosticTests.cs](../src/SphereNet.Tests/StairThrowDiagnosticTests.cs#L29) Bu yüzden sonuç “0 skipped” görünse de üç test fiilen çalışmadı.
- Trigger belgeleri güncel değil. Belgeler `NPCSeeWantItem` tetikleyicisini hâlâ eksik gösteriyor; fakat bağlantısı artık yapılmış. [Gerçek bağlantı](../src/SphereNet.Server/Program.EngineWiring.cs#L1660), [eski doküman](STUB_INVENTORY_TR.md#L37)

## Önerilen detaylı uygulama planı

### 1. Başlangıç bağlantılarını güvenceye alma — P1

Amaç: Sıralamaya bağlı sessiz bağlantı hatalarını tamamen görünür kılmak.

- Motor oluşturma ile motorlar arası bağlantı aşamalarını ayırın.
- Bütün nesneler oluşturulduktan sonra tek bir “wiring/finalization” adımı çalıştırın.
- `MovementEngine.SpellEngine`, `NpcAI.GetLightLevel` ve komut event’lerini bu aşamada bağlayın.
- Startup sonunda zorunlu bağlantıları doğrulayan fail-fast kontrol ekleyin.
- Ağ portu açmadan gerçek motor grafiğini kurabilen bir entegrasyon fixture’ı tasarlayın.

Kabul kriterleri:

- Hareket sırasında cast interruption gerçek startup grafiğiyle test edilmeli.
- Gece ışık seviyesinde NPC’nin ışık davranışı değişmeli.
- `.SAVE`, `.SHUTDOWN` ve `.BROADCAST` komutlarının gerçek handler’a ulaştığı doğrulanmalı.
- Zorunlu bir motor bağlantısı `null` kalırsa sunucu açık bir hata ile başlamamalı.

### 2. Yapılandırma sözleşmesini oluşturma — P1/P2

Amaç: “Ayar okunuyor ama etkisiz” durumunu tekrar oluşamayacak hale getirmek.

Her ayarı şu kategorilerden birine yerleştirin:

1. Çalışma zamanında uygulanıyor.
2. Eski ayar alias’ı olarak dönüştürülüyor.
3. Yalnız metadata.
4. Bilinçli şekilde desteklenmiyor/deprecated.
5. Bağlantısı eksik.

İlk dalgada bağlanması gerekenler:

- Tick periyodu ve oyun saati
- Sector sleep süresi
- Konuşma ve NPC duyma mesafeleri
- Görüş mesafesi ve üst sınırı
- Fame/karma limitleri
- Karakter silme bekleme süresi
- Ölü/canlı görünürlük kuralı
- HTTP servis açma/kapatma
- `MapReadId`, `MapSendId` ve sector boyutu

Kabul kriterleri:

- Her parse edilen ayarın statüsü otomatik testle doğrulanmalı.
- Ayar değiştirilince çalışma zamanında gözlenebilir davranış değişmeli.
- `TICKPERIOD` ve `ServerTickMs` için tek kanonik isim ve uyumluluk alias’ı belirlenmeli.
- Desteklenmeyen seçenekler başlangıç uyarısı üretmeli; sessizce yok sayılmamalı.

### 3. Güvenli save ve shutdown tasarımı — P1

- `.SHUTDOWN` önce yeni bağlantıları durdurmalı.
- Aktif istemcilere süreli kapanış bildirimi göndermeli.
- Ana oyun döngüsünde güvenli bir save bariyeri oluşturmalı.
- Save başarılıysa shutdown tamamlanmalı; başarısızsa açık hata ve yönetici kararı gerekmeli.
- Otomatik shutdown save davranışı yapılandırılabilir olmalı, güvenli varsayılan açık olmalı.
- Background/incremental save uygulanmadan önce tutarlı snapshot modeli tasarlanmalı. Canlı nesne koleksiyonlarını başka thread’den doğrudan serialize etmekten kaçınılmalı.
- Geçici dosyaya yazma ve atomik değiştirme korunmalı.

Kabul kriterleri:

- Save sırasında kapatma, save hatası ve ikinci shutdown isteği test edilmeli.
- Kapanıştan sonra yeniden yüklenen dünyada son hareket/eşya değişiklikleri bulunmalı.
- Yarım dosya veya eski/yeni dünyanın karıştığı save üretilememeli.

### 4. Yarım mekanikleri tam yaşam döngüsüne dönüştürme — P2

Sıralama önerisi:

1. Işıkların yanma süresi, charge tüketimi, otomatik sönme ve persistence.
2. Tarımın büyüme evreleri, görsel dönüşümü, yeniden hasat ve save/load davranışı.
3. Spell coverage matrisi.

Spell matrisi her büyü için şunları göstermeli:

- Script tanımı yüklü mü?
- Hedef tipi destekleniyor mu?
- Genel damage/heal/summon davranışı var mı?
- Özel okul mekaniği var mı?
- Persistence gereken etkiler kaydediliyor mu?
- Entegrasyon testi var mı?

Eksik büyüler tamamlanana kadar açıkça pasif işaretlenmeli; sessiz etkisizlik engellenmeli.

### 5. Atıl altyapılar hakkında karar verme — P2/P3

- `BotPerformanceGate`: Bot senaryolarına, tick histogramına ve CI pass/fail sonucuna bağlanmalı.
- Fast-walk stack: İstemci sürümü/era uyumlu tam anahtar rotasyonu uygulanmalı ya da ölü paket sınıfları kaldırılmalı. Mevcut zaman tabanlı hareket kısıtlaması korunmalı.
- `ExpansionInfo`: Feature negotiation için tek doğruluk kaynağı yapılmalı veya manuel startup maskesi lehine kaldırılmalı.
- `ExpressionGlobals` ve `ConditionalEvaluator`: Mevcut scripting hattıyla birleştirme ihtiyacı yoksa temizlenmeli.
- Kullanılmayan event’ler ya gerçek audit/metrics hattına bağlanmalı ya da API yüzeyinden çıkarılmalı.

### 6. Gerçek çalışma zamanı doğrulaması — P1/P2

Son doğrulama paketi şu sırayla hazırlanmalı:

- Portsuz composition-root testi
- Minimal kaynaklarla sunucu start/stop smoke testi
- Login → hareket → büyü → combat → loot → save → reload senaryosu
- Gece/gündüz ve NPC ışık senaryosu
- `.broadcast`, `.save`, `.shutdown` yönetici senaryosu
- Gerçek MUL/UOP fixture’larıyla hareket ve yükseklik kenar durumları
- Çoklu bot bağlantısı ve tick gecikme eşikleri
- Save sırasında kontrollü hata/crash enjeksiyonu
- Eksik harici fixture’ların gerçek `Skipped` olarak raporlanması

Önerdiğim sıra: **startup bağlantıları → shutdown/save → config sözleşmesi → yarım mekanikler → atıl altyapı temizliği → belge güncellemesi**. İlk üç aşama tamamlanmadan yeni oyun mekaniği eklemek, mevcut sessiz entegrasyon hatalarının sayısını artırabilir.
