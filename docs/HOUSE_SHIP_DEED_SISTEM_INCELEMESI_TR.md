# SphereNet House ve Ship Deed Sistemleri İncelemesi

## Kapsam

Bu inceleme aşağıdaki akışları kapsar:

- House ve ship deed oluşturma
- Deed üzerine çift tıklama
- Multi tanımının çözülmesi
- Hedefleme ve yerleştirme koordinatı
- Arazi, bölge, karakter, eşya ve başka multi kontrolleri
- House/ship ana nesnesinin ve bileşenlerinin oluşturulması
- İstemciye görünüm ve hareket paketlerinin gönderilmesi
- Sahiplik, anahtar, kapı, tabela, tillerman, ambar ve plank kullanımı
- House yönetimi ve custom house başlangıcı
- Ship hareket, dönüş, pilot ve dry-dock akışları
- Save/load ve yeniden deed oluşturma
- Eski ve yeni `multi.mul` formatlarıyla uyumluluk

İnceleme sırasında kaynak kod değiştirilmedi. Yalnızca bu Markdown raporu oluşturuldu.

## Kısa sonuç

House ve ship sistemlerinin büyük bölümü kod olarak mevcut; fakat mevcut `C:\sphereNetServer\mul` verisiyle yerleştirme yapılmasını engelleyen kesin ve bloklayıcı bir veri-formatı hatası bulunuyor.

Mevcut `MultiReader`, bütün `multi.mul` kayıtlarını eski 12-byte formatında okuyor. Kullanılan dosya ise High Seas ve sonrası 16-byte formatında. Kayıtlar yanlış adımlarla okunduğu için multi bileşen koordinatları bozuluyor, footprint binlerce tile büyüyor ve harita sınırı kontrolü hem evi hem gemiyi reddediyor.

Bu hata kullanıcının gördüğü “Cannot place house/ship here” davranışının ana nedenidir.

Format hatasının arkasında, sistem kullanılabilir hale geldiğinde karşılaşılacak başka önemli eksikler de vardır:

1. Script deed’leri `ID=i_deed` üzerinden yalnız görsel ID’yi devralıyor; `t_deed` türünü devralmıyor.
2. `[MULTIDEF]` script metadata’sı runtime `MultiRegistry` içine yüklenmiyor.
3. Multi geometrisi ile gerçek/dinamik bileşenler birbirine karıştırılmış.
4. Standart custom foundation deed’leri `MultiCustom` olarak algılanmıyor.
5. Multi yerleştirme önizleme paketi `0x99` uygulanmamış.
6. Tıklanan nokta doğrudan anchor kabul ediliyor; gerekli multi koordinat düzeltmesi yok.
7. Gerçek save/load turunda house/ship türü ve multi kimliği güvenilir şekilde korunmuyor.
8. Classic small ship’in raw multi ID’si `0` olduğu için dry-dock sonrası deed tekrar açılamıyor.
9. Ship hareket paketinde hareket eden bileşen listesi yok.
10. Başarısızlık nedenleri tek bir genel mesaja indirgeniyor; limit, format, su, zemin veya overlap ayrıştırılamıyor.

## Kullanılan MUL verisindeki kesin kanıt

İncelenen dosyalar:

- `C:\sphereNetServer\mul\multi.idx`
- `C:\sphereNetServer\mul\multi.mul`
- `C:\sphereNetServer\mul\tiledata.mul`

Dosya bilgileri:

| Dosya | Boyut | Sonuç |
|---|---:|---|
| `multi.idx` | 104.448 byte | 8.704 index kaydı |
| `multi.mul` | 993.184 byte | Multi component verisi |
| `tiledata.mul` | 3.188.736 byte | High Seas tiledata boyutu |

`multi.idx` içindeki ilk kayıt 608 byte uzunluğunda:

- `608 % 16 = 0`
- `608 % 12 = 8`

Dolayısıyla kayıt 16-byte High Seas component kayıtlarından oluşuyor. Mevcut okuyucu ise sabit olarak 12 byte kullanıyor:

- [Sabit 12-byte component boyutu](../src/SphereNet.MapData/Multi/MultiReader.cs#L14)
- [Component sayısının 12’ye bölünmesi](../src/SphereNet.MapData/Multi/MultiReader.cs#L60)
- [Her kayıtta yalnız eski alanların okunması](../src/SphereNet.MapData/Multi/MultiReader.cs#L64)

Source-X iki formatı ayrı ele alıyor:

- [Eski `CUOMultiItemRec` 12-byte düzeni](../oldSphere/Source-X-full/src/game/uo_files/CUOMultiItemRec.h#L28)
- [High Seas `CUOMultiItemRec_HS` 16-byte düzeni](../oldSphere/Source-X-full/src/game/uo_files/CUOMultiItemRec.h#L39)
- [Format algılama](../oldSphere/Source-X-full/src/common/CUOInstall.cpp#L110)
- [Format bazında farklı kayıt boyutuyla okuma](../oldSphere/Source-X-full/src/common/CServerMap.cpp#L618)

### Yanlış okumanın gerçek footprint’e etkisi

Aynı kayıtlar 12 ve 16 byte olarak yorumlandığında:

| Multi ID | Yapı | Yorum | Component | X sınırı | Y sınırı | Z sınırı |
|---:|---|---:|---:|---:|---:|---:|
| `0x0000` | Small ship north | Mevcut 12-byte | 50 | `-2..2` | `-3..16092` | `-2..1` |
| `0x0000` | Small ship north | Doğru 16-byte | 38 | `-2..2` | `-5..5` | `0..0` |
| `0x0008` | Large ship grubu | Mevcut 12-byte | 57 | `-2..2` | `-5..16094` | `-2..2` |
| `0x0008` | Large ship grubu | Doğru 16-byte | 43 | `-2..2` | `-5..6` | `0..0` |
| `0x0064` | Small stone/plaster house | Mevcut 12-byte | 197 | `-3..36` | `-3..2968` | `-3..36` |
| `0x0064` | Small stone/plaster house | Doğru 16-byte | 148 | `-3..4` | `-3..4` | `0..36` |
| `0x1404` | Foundation grubu | Mevcut 12-byte | 102 | `-4..7` | `-3..12788` | `-4..7` |
| `0x1404` | Foundation grubu | Doğru 16-byte | 77 | `-4..4` | `-3..3` | `0..7` |

`PlaceHouse` ve `PlaceShip`, multi sınırlarının haritanın dışına taşıp taşmadığını yerleştirmeden önce kontrol ediyor:

- [House sınır kontrolü](../src/SphereNet.Game/Housing/HousingEngine.cs#L564)
- [Ship sınır kontrolü](../src/SphereNet.Game/Ships/ShipEngine.cs#L89)

Örneğin small ship için hatalı `MaxY=16092` olduğundan, yüksekliği 4096 olan bir haritanın herhangi bir normal noktasında şu kontrol her zaman başarısız olur:

```text
targetY + 16092 >= 4096
```

House tarafında small house `MaxY=2968` olarak okunuyor. `Y > 1127` olan bölgelerin tamamında daha zemin kontrolüne geçmeden harita sınırı nedeniyle reddediliyor. Daha kuzeyde ise binlerce tile büyüklüğündeki sahte footprint, NoBuild/zemin/karakter kontrollerinden birine takılıyor.

## Mevcut çift tıklama ve placement akışı

Deed’in mevcut akışı:

1. Item görünürlük ve erişim kontrolünden geçer.
2. `@DClick` trigger’ı çalışır.
3. Item türü `ItemType.Deed` ise deed handler’a girer.
4. `HOUSE_MULTI_BASEID`, `SHIP_MULTI_BASEID`, `MORE1_DEFNAME`, `MORE` veya `More1` üzerinden raw multi ID çözülür.
5. Normal `0x6C` target cursor açılır.
6. İstemcinin gönderdiği X/Y/Z doğrudan house/ship anchor olarak kullanılır.
7. Placement motoru limit ve arazi kontrollerini yapar.
8. Başarılıysa multi oluşturulur ve deed silinir.
9. Herhangi bir aşama başarısızsa genel “Cannot place…” mesajı gösterilir.

İlgili akış:

- [Deed switch kolu](../src/SphereNet.Game/Clients/ClientItemUseHandler.cs#L1170)
- [Multi ID çözümleme](../src/SphereNet.Game/Clients/ClientItemUseHandler.cs#L2969)
- [Target koordinatının doğrudan kullanılması](../src/SphereNet.Game/Clients/ClientItemUseHandler.cs#L1191)
- [Tek genel başarısızlık mesajı](../src/SphereNet.Game/Clients/ClientItemUseHandler.cs#L1217)

## Önceliklendirilmiş bulgular

### Bloklayıcı — P0/P1

| Bulgu | Etki |
|---|---|
| `multi.mul` yalnız 12-byte formatında okunuyor. | High Seas+ verilerinde bütün house/ship footprint’leri bozuluyor; placement yapılamıyor. |
| `ID=i_deed` yalnız display ID olarak ele alınıyor, item type inheritance yapılmıyor. | Standart script house/ship deed’leri `ItemType.Normal` kalabilir ve deed handler’a hiç girmeyebilir. |
| Runtime `MultiRegistry` yalnız `multi.mul` okuyor; `[MULTIDEF]` metadata’sını birleştirmiyor. | Type, name, custom-house türü, component, region, storage, vendor ve ship speed bilgileri kayboluyor. |
| Multi raw ID, dünya item `BaseId` alanında doğrudan tutuluyor. | İstemci multi art ID’si ve server raw multi index’i karışıyor; görünüm ve persistence güvenilir değil. |
| World save item türünü yazmıyor ve loader multi türünü raw ID’den yeniden kuramıyor. | Restart sonrasında house/ship registry’ye geri alınamayabilir. |

### Yüksek — P1

| Bulgu | Etki |
|---|---|
| `0x99` multi placement/preview paketi yok. | Oyuncu yapının footprint’ini ve doğru anchor noktasını görmeden normal target cursor kullanıyor. |
| Source-X multi anchor Y düzeltmesi uygulanmıyor. | Format düzeldikten sonra bile yapı tıklanan noktaya göre kaymış yerleşebilir. |
| Geometry component ile dinamik fixture component aynı listede tutuluyor. | Görsel multi karoları gerçek world item’larına dönüşüyor; buna karşılık kapı, tabela, tillerman, ambar ve plank eksik kalıyor. |
| Yerleştirme sonucu yalnız `null/bool`. | Limit, NoBuild, su, eğim, overlap, component veya script veto nedenleri ayrıştırılamıyor. |
| Target callback yalnız deed’in silinip silinmediğini yeniden kontrol ediyor. | Cursor açıldıktan sonra deed taşınabilir/takas edilebilir; sahiplik, erişim, mesafe ve LOS yeniden doğrulanmıyor. |
| Custom foundation türü yalnız deed üzerindeki `CUSTOMHOUSE` tag’inden belirleniyor. | İlk kez oluşturulan standart foundation deed’leri custom house yerine klasik multi olarak açılıyor. |
| Small ship raw ID `0` redeed tag/parser yolunda geçersiz sayılıyor. | Classic small ship dry-dock sonrası üretilen deed tekrar açılamıyor. |
| `0xF6` ship move paketi component listesi taşımıyor. | Gemi ile yolcu/eşya/fixture hareketi istemcide eksik, gecikmeli veya titrek görünebilir. |

### Orta — P2

| Bulgu | Etki |
|---|---|
| House placement NoBuild kontrolü footprint’in dört tarafına 5 tile uyguluyor. | Source-X varsayılanından daha geniş alan reddedilebilir. |
| House terrain/item yoğun kontrolünde Source-X’in 1-tile dış sınırı aynı şekilde uygulanmıyor. | Bazı bloklar gözden kaçabilir; bazı NoBuild alanları ise gereğinden fazla reddedilebilir. |
| Ship su kontrolü yalnız wet land tile’a bakıyor. | Wet static ile temsil edilen bazı geçerli su yüzeyleri reddedilebilir. |
| House storage her yapıda varsayılan 400. | Scriptteki küçük ev, büyük ev, keep ve castle storage farkları uygulanmıyor. |
| `BaseVendors`, `MULTIREGION`, `REGIONFLAGS`, `SHIPSPEED`, `SPEEDMODE`, `HEIGHT` uygulanmıyor. | Yapı ve gemi tipleri scriptte tanımlanan davranışlarını kaybediyor. |
| House türü daima Private başlıyor ve public/private/guild yönetim akışı yok. | `HouseType.Public` ve `HouseType.Guild` altyapısı var fakat normal oynanıştan kullanılamıyor. |
| Access-only ve vendor listeleri modelde var fakat standart house gump’ında tam yönetilemiyor. | Bazı yetki yüzeyleri yalnız script verb/save-load tarafında kalıyor. |
| House decay yenilemesi “owner eve girdiğinde” değil, house gump açıldığında yapılıyor. | Owner evi düzenli kullansa bile tabelayı/gump’ı açmazsa decay ilerleyebilir. |
| `MAXHOUSESGUILD` config dosyasında belgelenmiş fakat config/runtime modeli bulunmuyor. | Guild house limiti uygulanmıyor. |
| House/ship owner UUID tag’i yazılıyor fakat restore sırasında kullanılmıyor. | Serial migration veya remap durumlarında sahiplik onarılamıyor. |
| Ship region/deck üyeliği gerçek deck karoları yerine dikdörtgen footprint ile belirleniyor. | Hull çevresindeki boş dikdörtgen hücreler gemi üstü sayılabilir. |
| High Seas `shipAccess` alanı okunmuyor. | Yeni gemilerin özel giriş/çıkış component semantiği kayboluyor. |

## Deed tanımı ve item type problemi

Standart house deed örneği:

- [House deed `ID=i_deed`](../oldSphere/Scripts-X-main/items/i_provisions_deed.scp#L77)
- [Ship deed `ID=i_deed_ship`](../oldSphere/Scripts-X-main/items/i_provisions_deed.scp#L317)
- Base deed üzerindeki [`TYPE=t_deed`](../oldSphere/Scripts-X-main/items/i_provisions_deed.scp#L7)

Child deed tanımlarının çoğunda doğrudan `TYPE=t_deed` yok. Source-X semantiğinde `ID=i_deed`, base item davranışını devralır.

SphereNet `DefinitionLoader`, `ID`/`DISPID` defname referansını yalnız display ID zinciri olarak işliyor:

- [Display reference çözümleme](../src/SphereNet.Game/Definitions/DefinitionLoader.cs#L426)
- [Item type’ın doğrudan child def’ten instance’a yazılması](../src/SphereNet.Game/Definitions/ItemDefHelper.cs#L51)
- [ItemDef varsayılan türü `Normal`](../src/SphereNet.Scripting/Definitions/ItemDef.cs#L13)

`DUPEITEM` için type inheritance mevcut olsa da `ID=i_deed`/`DisplayIdRef` zinciri için aynı inheritance yapılmıyor:

- [Yalnız `DupItemId` inheritance döngüsü](../src/SphereNet.Game/Definitions/DefinitionLoader.cs#L930)

Bu nedenle gerçek scriptten oluşturulan deed için şu zincir test edilmelidir:

```text
i_deed_stone_and_plaster_house
  -> ID=i_deed
  -> display 0x14EF
  -> inherited TYPE=t_deed
  -> @Create MORE=m_stone_and_plaster_house
  -> ItemType.Deed handler
```

Mevcut testler bu zinciri gerçek script üzerinden çalıştırmıyor; test item’ına `ItemType.Deed` elle atanıyor.

## MULTIDEF metadata ve component problemi

Runtime registry yalnız `multi.mul` içindeki component kayıtlarını yükler:

- [MultiRegistry.LoadFromMapData](../src/SphereNet.Game/Housing/HousingEngine.cs#L470)
- [Startup sırasında yalnız map data yüklenmesi](../src/SphereNet.Server/Program.EngineWiring.cs#L2307)

Fakat gerçek davranış metadata’sı script `[MULTIDEF]` bölümlerinde:

```text
TYPE=t_multi / t_multi_custom / t_ship
NAME=...
MULTIREGION=...
REGIONFLAGS=...
TSPEECH=...
COMPONENT=...
BaseStorage=...
BaseVendors=...
SHIPSPEED=...
SPEEDMODE=...
HEIGHT=...
```

Örnekler:

- [Standart house MULTIDEF](../oldSphere/Scripts-X-main/multis/m_houses.scp#L7)
- [Standart ship MULTIDEF](../oldSphere/Scripts-X-main/multis/m_ships_base.scp#L7)
- [Custom foundation MULTIDEF](../oldSphere/Scripts-X-main/multis/m_foundations.scp#L9)

### İki component türü ayrılmalı

`multi.mul` component’leri:

- Yapının istemcide çizilen geometrisidir.
- Collision, LOS, footprint ve görünüm için kullanılır.
- Her biri ayrı kalıcı world item olmamalıdır.

`[MULTIDEF] COMPONENT` satırları:

- Kapı
- House sign
- Tillerman
- Hatch/hold
- Plank/ship side
- Telepad veya özel fixture

gibi etkileşimli gerçek item’lardır. Bunların:

- ItemDef metadata’sıyla oluşturulması,
- `@Create` trigger’larının çalışması,
- multiye linklenmesi,
- save/load edilmesi,
- redeed sırasında kaldırılması

gerekir.

Mevcut sistem `multi.mul` içindeki görünür geometriyi ayrı item’lara dönüştürüyor:

- [House component oluşturma](../src/SphereNet.Game/Housing/HousingEngine.cs#L600)
- [Ship component oluşturma](../src/SphereNet.Game/Ships/ShipEngine.cs#L114)

Script `COMPONENT` satırlarını ise runtime registry’ye hiç almıyor. Sonuç olarak:

- House kapısı ve tabelası oluşmayabilir.
- House management gump’a erişilecek sign bulunmayabilir.
- Ship tillerman, hold ve plank oluşmayabilir.
- Ship key üretimini başlatacak locked component bulunmayabilir.
- Multi görünümü ile fiziksel item’lar çift çizilebilir veya yanlış çizilebilir.
- Her house yüzlerce gereksiz world item üretebilir.

## Client multi ID ve raw multi ID ayrımı

`multi.idx`/`multi.mul` içindeki raw multi ID ile istemciye gönderilen multi art ID aynı kavram değildir.

Mevcut placement:

- House multi item `BaseId = multiId` yapıyor. [House](../src/SphereNet.Game/Housing/HousingEngine.cs#L580)
- Ship multi item `BaseId = orientedId` yapıyor. [Ship](../src/SphereNet.Game/Ships/ShipEngine.cs#L97)
- Network katmanı `DispIdFull` değerini dönüştürmeden gönderiyor. [World item packet](../src/SphereNet.Game/Clients/GameClient.PacketHelpers.cs#L646)

Sağlıklı modelde aşağıdaki değerler ayrılmalıdır:

- `RawMultiId`: Registry ve `multi.idx` lookup değeri; örneğin small house `0x64`, small ship north `0`.
- `ClientMultiArtId`: İstemciye gönderilecek multi art kimliği; klasik UO protokolündeki multi tabanı ile encode edilir.
- `StructureKind`: House, CustomHouse veya Ship.
- `ScriptMultiDef`: `m_stone_and_plaster_house`, `m_small_ship_n` gibi resource kimliği.

Registry lookup, speech trigger, movement, save/load ve packet üretimi bu alanların hangisini kullandığını açıkça belirtmelidir.

## Placement protokolü ve koordinat problemi

Paket tablosu `0x99` Multi Target paketini biliyor:

- [PacketDefinitions `0x99`](../src/SphereNet.Network/Packets/PacketDefinitions.cs#L116)

Ancak bu paket için outgoing packet sınıfı veya deed placement kullanımı bulunmuyor. Deed handler normal `PacketTarget` gönderiyor:

- [Normal target packet](../src/SphereNet.Network/Packets/Outgoing/ExtendedPackets.cs#L238)
- [Deed target cursor](../src/SphereNet.Game/Clients/ClientItemUseHandler.cs#L1191)

Source-X, gerçek multi için istemciden gelen noktanın Y koordinatını multi footprint’e göre düzeltiyor:

- [Source-X placement koordinat düzeltmesi](../oldSphere/Source-X-full/src/game/items/CItemMulti.cpp#L3281)

SphereNet tıklanan X/Y/Z’yi doğrudan anchor olarak kullanıyor. Bu nedenle format düzeltildikten sonra bile:

- House/gemi hedef noktasına göre kaymış çıkabilir.
- Oyuncu footprint’in içine denk gelebilir ve kendi karakteri placement’ı engelleyebilir.
- Tıklanan static/item Z’si anchor Z kabul edilerek düz arazide yanlış slope sonucu oluşabilir.

## Placement doğrulama ve mesaj eksikleri

House ve ship motorları başarısızlıkta yalnız `null` veya `false` döndürüyor. Kullanıcıya hangi kontrolün başarısız olduğu bildirilmiyor.

Önerilen sonuç nedenleri:

- `DeedTypeInvalid`
- `DeedNotOwnedOrAccessible`
- `MultiReferenceMissing`
- `MultiDefinitionMissing`
- `MultiDataFormatInvalid`
- `PlayerLimitReached`
- `AccountLimitReached`
- `GuildLimitReached`
- `TargetOutOfRange`
- `TargetOutOfSight`
- `TargetOutOfMap`
- `NoBuildRegion`
- `TerrainBlocked`
- `TerrainNotFlat`
- `CharacterBlocking`
- `ItemBlocking`
- `HouseOverlap`
- `ShipOverlap`
- `WaterRequired`
- `WaterObstructed`
- `ScriptVeto`
- `CreationFailed`

Her sonuç:

- Ayrı oyuncu mesajına,
- Multi ID/defname ve koordinat içeren structured log’a,
- Test edilebilir bir result enum’una

bağlanmalıdır.

### House placement farkları

Mevcut house kontrolü:

- NoBuild için footprint’in dört tarafına 5 tile ekliyor. [HousingEngine](../src/SphereNet.Game/Housing/HousingEngine.cs#L719)
- Terrain/passability kontrolünü yalnız footprint içinde yapıyor. [HousingEngine](../src/SphereNet.Game/Housing/HousingEngine.cs#L734)

Source-X varsayılanları:

- Block radius: `-1,-1,1,1`
- Multi/NoBuild radius: `0,-5,0,5`

[Source-X radius başlangıç değerleri](../oldSphere/Source-X-full/src/game/items/CItemMulti.cpp#L3269)

Bu iki davranış aynı değildir. Mevcut uygulama bazı yönlerde gereğinden fazla NoBuild reddi verirken, footprint dışındaki bir tile’lık fiziksel engel kontrolünü eksik bırakabilir.

### Ship su kontrolü

Mevcut `IsWaterAt` yalnız terrain tile’ın `Wet` flag’ine bakıyor:

- [IsWaterAt](../src/SphereNet.Game/Ships/ShipEngine.cs#L836)

Genel uyumluluk için:

- Wet land
- Wet static/surface
- Blocking dock/rock/static
- Su düzlemi Z’si
- Başka ship hull’ları
- High Seas access component’leri

tek bir navigable-water sonucu altında değerlendirilmelidir.

## Target callback güvenlik ve tutarlılık eksikleri

Cursor açılırken deed erişilebilirlik kontrolünden geçiyor. Fakat target cevabı geldiğinde yalnız `deedItem.IsDeleted` kontrol ediliyor:

- [Target callback](../src/SphereNet.Game/Clients/ClientItemUseHandler.cs#L1191)

Target cevabında yeniden doğrulanması gerekenler:

- Deed hâlâ aynı oyuncunun backpack/bank/erişilebilir container zincirinde mi?
- Deed başka oyuncuya takas edildi mi?
- Oyuncu hâlâ aynı map üzerinde mi?
- Hedef izin verilen yerleştirme mesafesinde mi?
- Hedef görülebiliyor mu?
- Hedef koordinatı packet spoof ile view range dışına çıkarılmış mı?
- Oyuncu ölü/frozen/jailed duruma geçti mi?
- House/ship limitleri cursor açıldıktan sonra değişti mi?

Deed, validation ve creation işlemi atomik bir placement transaction gibi ele alınmalıdır. Multi tamamen oluşturulmadan deed tüketilmemelidir; component oluşturma yarıda kalırsa bütün kısmi nesneler geri alınmalıdır.

## Custom house eksikleri

Custom house motoru, tasarım oturumu ve `0xD8` stream altyapısı mevcut. Ancak başlangıç deed algılama yolu eksik:

- Handler yalnız deed üzerindeki `CUSTOMHOUSE` tag’ine bakıyor. [ClientItemUseHandler](../src/SphereNet.Game/Clients/ClientItemUseHandler.cs#L1173)
- Standart foundation deed yalnız `MORE=m_foundation_*` ayarlıyor. [Foundation deed](../oldSphere/Scripts-X-main/items/i_provisions_deed.scp#L668)
- Foundation’ın `TYPE=t_multi_custom` bilgisi MULTIDEF içindedir. [Foundation MULTIDEF](../oldSphere/Scripts-X-main/multis/m_foundations.scp#L9)

İlk foundation deed’de `CUSTOMHOUSE` tag’i bulunmadığı için yapı `MultiCustom` yerine klasik `Multi` olarak yerleştirilebilir. Tag yalnız mevcut custom house redeed edildiğinde ekleniyor:

- [Redeed sırasında `CUSTOMHOUSE` tag’i](../src/SphereNet.Game/Housing/HousingEngine.cs#L352)

Custom türü deed tag’inden tahmin edilmemeli; resolved MULTIDEF type üzerinden belirlenmelidir.

## House yaşam döngüsü eksikleri

### Mevcut ve çalışan altyapı

- Player/account house limitleri
- Owner/co-owner/friend/ban/access/vendor veri modeli
- House transfer
- Lockdown ve secure container
- Anahtar üretme
- Demolish/redeed
- Moving crate ile house içi eşya koruma
- Custom house edit oturumu
- Dynamic house region
- Decay stage ve save tag’leri
- Yönetim gump’ı

### Eksik veya bağlı olmayan bölümler

- Script `BaseStorage` değerleri yüklenmiyor; bütün house’lar 400 storage ile başlıyor. [Varsayılan](../src/SphereNet.Game/Housing/HousingEngine.cs#L119)
- `BaseVendors` uygulanmıyor.
- House sign/door script component’leri oluşturulmuyor.
- Public/private/guild tipi normal oyuncu akışından değiştirilemiyor.
- Access-only yönetimi standart gump’ta eksik.
- Vendor kayıt modeli var, ancak house vendor yaşam döngüsüyle tam bağlı değil.
- `MAXHOUSESGUILD` belgelenmiş fakat config/runtime’da yok.
- Owner eve girince decay refresh edilmesi yerine gump açılması bekleniyor. [Gump refresh](../src/SphereNet.Game/Clients/ClientWorldFeaturesHandler.cs#L1385)
- Region footprint script `MULTIREGION` yerine binary component bounds’tan çıkarılıyor.
- `REGIONFLAGS` metadata’sı tam uygulanmıyor.
- Tabela yoksa yönetim gump’ına doğal erişim kalmayabilir.

## Ship yaşam döngüsü eksikleri

### Mevcut ve çalışan altyapı

- Player/account ship limitleri
- Dört yönlü multi seçimi
- Water/obstacle/ship overlap kontrollerinin temeli
- Ship region
- Anchor raise/drop
- Hareket, dönüş ve yön komutları
- Wheel pilot (`0xBF 0x33`)
- Ban/boarding kontrolü
- Ship key modeli
- Hold/plank/tillerman model alanları
- Dry-dock/redeed
- Hold ve deck eşyalarını moving crate’e taşıma
- Owner, pilot, speed ve component save tag’leri

### Eksik veya bağlı olmayan bölümler

- Script component’leri yüklenmediği için tillerman, hold, hatch ve plank oluşmayabilir.
- `ClassifyShipComponent`, gerçek script component listesi olmadan yalnız multi geometrisindeki tile’lara uygulanıyor.
- `needsKey` locked fixture bulunmazsa false kalıyor ve ship key oluşturulmuyor.
- Script `SHIPSPEED`, `SPEEDMODE` ve `HEIGHT` değerleri kullanılmıyor.
- High Seas `shipAccess` alanı kayboluyor.
- Deck membership dikdörtgen region üzerinden hesaplanıyor; gerçek deck geometrisi kullanılmıyor.
- `0xF6` paketinde component ve yolcu/eşya listesi bulunmuyor. [PacketBoatSmoothMove](../src/SphereNet.Network/Packets/Outgoing/GamePackets.cs#L741)
- Movement sonrası component’lerin istemci güncellemesi Source-X packet paritesinde değil.
- Raw multi ID `0` generated redeed yolunda geçersiz kabul ediliyor:
  - [`TryParseDeedMultiId` sıfırı reddediyor](../src/SphereNet.Game/Clients/ClientItemUseHandler.cs#L3021)
  - [Fallback `targetId == 0` durumunda başarısız](../src/SphereNet.Game/Clients/ClientItemUseHandler.cs#L3006)
- Ship speed defaults her gemide aynı başlıyor. [Ship defaults](../src/SphereNet.Game/Ships/Ship.cs#L23)

## Save/load problemi

House ve ship motorları runtime metadata’yı item tag’lerine yazıyor:

- [House SerializeAllToTags](../src/SphereNet.Game/Housing/HousingEngine.cs#L1086)
- [Ship SerializeAllToTags](../src/SphereNet.Game/Ships/ShipEngine.cs#L1161)
- [Save öncesi çağrılar](../src/SphereNet.Server/Program.Persistence.cs#L233)

Ancak genel item saver:

- Item `TYPE` değerini yazmıyor.
- `BaseId` üzerinden normal ITEMDEF defname arıyor.
- Multi raw ID ile normal item graphic ID arasında ayrım yapmıyor.

[WorldSaver item yazımı](../src/SphereNet.Persistence/Save/WorldSaver.cs#L535)

Load sonrasında:

- Raw `BaseId=0x64` gibi bir değer normal ITEMDEF olarak yorumlanabilir.
- Instance `ItemType.Multi`, `MultiCustom` veya `Ship` olarak yeniden kurulmayabilir.
- `HousingEngine.DeserializeFromWorld` yalnız Multi/MultiCustom item’ları tarar. [House restore](../src/SphereNet.Game/Housing/HousingEngine.cs#L1146)
- `ShipEngine.DeserializeFromWorld` yalnız Ship item’ları tarar. [Ship restore](../src/SphereNet.Game/Ships/ShipEngine.cs#L1200)

Mevcut persistence testleri aynı canlı world içindeki item’ları serialize edip ikinci engine ile tekrar okuyor. Gerçek:

```text
WorldSaver -> dosya -> yeni GameWorld -> WorldLoader -> Housing/Ship Deserialize
```

turunu house/ship için doğrulamıyor.

### Gerekli save migration modeli

Her placed structure için açık biçimde saklanması gerekenler:

- `STRUCTURE.KIND=HOUSE|CUSTOMHOUSE|SHIP`
- `STRUCTURE.MULTIID=<raw id>`
- `STRUCTURE.MULTIDEF=<defname>`
- Owner serial ve owner UUID
- Facing/oriented raw ID
- Script metadata sürümü
- Dynamic component UUID/serial listesi
- Custom design revision ve tile’lar

Eski save migration:

1. `HOUSE.*` tag’i taşıyan normal item’ı house adayı say.
2. `SHIP.*` tag’i taşıyan normal item’ı ship adayı say.
3. Raw/encoded ID’yi normalize et.
4. Instance type’ı onar.
5. Eksik/stale dynamic component’leri kontrollü şekilde yeniden üret.
6. Aynı component’i iki kez oluşturmadan idempotent restore yap.
7. Yapılan migration’ı logla ve yeni formatta kaydet.

## Test kapsamı değerlendirmesi

House/ship filtreli mevcut test sonucu:

```text
96 başarılı
0 başarısız
0 atlanan
```

Bu testler motor içi davranış için değerlidir. Fakat ana runtime problemi yakalanmıyor çünkü:

- `MultiReader` için test bulunmuyor.
- Testlerin çoğu `MultiRegistry` içine elle oluşturulmuş birkaç component ekliyor.
- `MapData` çoğunlukla `null`; gerçek terrain/static kontrolü devre dışı.
- Gerçek Source-X deed scripti üzerinden `ItemType.Deed` inheritance testi yok.
- Gerçek `[MULTIDEF]` overlay testi yok.
- `0x99` placement preview testi yok.
- Gerçek saver/loader process boundary turu yok.
- High Seas 16-byte fixture testi yok.
- Eski 12-byte multi fixture testi yok.
- Tüm house/ship MULTIDEF’lerini tarayan consistency testi yok.
- Placed multi’nin client packet ID’sini doğrulayan test yok.

Mevcut ID-zero testi yalnız ilk oluşturulan deed üzerinde `MORE=m_small_ship_n` yolunu kapsıyor:

- [Ship ID-zero testi](../src/SphereNet.Tests/HousingShipIntegrityTests.cs#L96)

Dry-dock ile üretilen `SHIP_MULTI_BASEID=0` + `More1=0` deed yolunu kapsamıyor.

## Tüm sürümlerde çalışma hedefi

“Hepsinde çalışsın” hedefi için destek matrisi açık olmalıdır:

### Veri formatları

| Veri ailesi | Component boyutu | Zorunlu durum |
|---|---:|---|
| Original/pre-High Seas | 12 byte | Desteklenmeli |
| High Seas ve sonrası | 16 byte | Desteklenmeli |
| Bozuk/karışık/ambiguous dosya | Belirsiz | Sessiz yanlış okuma yerine startup hatası veya explicit override |

### Multi tipleri

- Klasik house
- Keep/castle
- Tent/camp ve diğer deed multi’leri
- Custom foundation
- Classic small/medium/large ship
- Dragon ship
- Orc/rowboat/gargoyle/tokuno/britannian ship
- Dört yönlü ship varyantları
- Raw ID’si `0` olan multi
- Scriptte olup binary dosyada olmayan özel multi
- Binary dosyada olup script metadata’sı eksik multi

### Haritalar

- Map 0–5
- Farklı map boyutları
- Negatif Z su düzlemleri
- Coast/dock/static water kenarları
- NoBuild ve normal wilderness bölgeleri

### İstemci yolları

- Normal klasik target cevabı fallback’i
- `0x99` multi preview destekleyen client
- ClassicUO
- Projenin desteklediği legacy/SA/HS feature seviyeleri
- Smooth ship move destekleyen ve desteklemeyen client fallback’i

## Önerilen uygulama planı

### Aşama 1 — Multi dosya okuyucusunu format güvenli hale getirme

1. `MultiFormat` modelini tanımlayın:
   - `Auto`
   - `Original12`
   - `HighSeas16`
2. Auto detection yalnız tek kaydın bölünebilirliğine güvenmemeli.
3. Birden fazla non-empty index kaydı üzerinde:
   - Length `% 12`
   - Length `% 16`
   - Component ID geçerliliği
   - X/Y/Z plausibility
   - Component count
   değerlendirilmelidir.
4. High Seas kaydında ek `shipAccess` alanı okunmalıdır.
5. Ambiguous dosyada config override kullanılmalı veya açık startup hatası verilmelidir.
6. Registry’ye eklemeden önce sanity guard uygulanmalıdır:
   - Mantıksız component count
   - Aşırı X/Y/Z offset
   - Harita boyutundan büyük footprint
   - Eksik/bozuk idx offset
7. Startup logunda şu bilgiler bulunmalıdır:
   - Seçilen format
   - Index kayıt sayısı
   - Geçerli/bozuk/boş multi sayısı
   - Örnek bilinen multi bounds

Kabul kriterleri:

- `0x0000` mevcut dosyada `X=-2..2`, `Y=-5..5` okunmalı.
- `0x0064` mevcut dosyada `X=-3..4`, `Y=-3..4` okunmalı.
- Eski 12-byte fixture aynı doğru sonuçları vermeli.
- Yanlış format seçilirse test açık biçimde başarısız olmalı.
- Bozuk bounds placement aşamasına kadar taşınmamalı.

### Aşama 2 — Kanonik multi kimlik modeli

1. Raw registry ID, client art ID ve script defname ayrılmalı.
2. Tek normalize/encode/decode katmanı oluşturulmalı.
3. Aşağıdaki tüketiciler kanonik API kullanmalı:
   - Placement
   - Direction/orientation
   - Client packet
   - Region
   - Speech trigger
   - Save/load
   - Redeed
4. Raw ID `0` geçerli değer olarak ele alınmalı.
5. “0 = yok” için nullable/explicit result kullanılmalı.

Kabul kriterleri:

- Small ship raw ID `0` ilk deed ve redeed sonrası çalışmalı.
- House/ship istemciye doğru multi art ID ile gönderilmeli.
- Registry hiçbir yerde client art ID ile yanlış lookup yapmamalı.

### Aşama 3 — ITEMDEF ve MULTIDEF script birleşimi

1. `ID=i_deed` inheritance zincirinde en az aşağıdakiler devralınmalı:
   - Type
   - TDATA
   - Layer
   - Gerekli base davranış metadata’sı
2. Child’ın açıkça verdiği değerler base değeri ezmeli.
3. `[MULTIDEF]` için runtime model oluşturulmalı:
   - Type
   - Name
   - MultiRegion
   - RegionFlags
   - Speech resources
   - Dynamic components
   - BaseStorage/BaseVendors
   - Ship speed/height/mode
4. Binary geometri ve script metadata aynı raw multi ID altında merge edilmeli.
5. Script-only multi için geometri/placement davranışı açıkça tanımlanmalı.

Kabul kriterleri:

- Gerçek `i_deed_stone_and_plaster_house` instance’ı `ItemType.Deed` olmalı.
- Gerçek `i_deed_small_ship_n` doğru raw ID `0` çözmeli.
- Foundation deed, MULTIDEF type üzerinden `MultiCustom` olmalı.
- Small house storage 489, ilgili büyük yapılar scriptteki storage değerini almalı.

### Aşama 4 — Geometry ve dynamic fixture ayrımı

1. Binary geometry yalnız:
   - Client rendering kimliği
   - Footprint
   - Collision
   - LOS
   - Deck/surface
   için kullanılmalı.
2. Script `COMPONENT` satırları gerçek world item üretmeli.
3. Dynamic component oluştururken tam ItemDef metadata uygulanmalı.
4. `@Create` trigger çalışmalı.
5. Component transaction uygulanmalı:
   - Bir fixture başarısızsa multi ve önceki fixture’lar rollback edilmeli.
6. House:
   - Door
   - Sign
   - Telepad/fixture
7. Ship:
   - Tillerman
   - Hold/hatch
   - Plank/side
   - High Seas access/rope
8. Redeed/demolish component cleanup idempotent olmalı.

Kabul kriterleri:

- Standart house yüzlerce geometry item üretmemeli.
- House sign ve door gerçek item olarak oluşmalı.
- Ship tillerman, hold ve iki plank doğru tip/link ile oluşmalı.
- Ship anahtarları pack ve bank’a gitmeli.
- Redeed sonrasında hiçbir ghost component/region kalmamalı.

### Aşama 5 — Placement protokolü ve koordinat düzeltmesi

1. `0x99` multi placement packet uygulanmalı.
2. Client capability’ye göre:
   - Multi preview yolu
   - Güvenli normal target fallback’i
   seçilmeli.
3. Multi türüne göre doğru cursor art ID gönderilmeli.
4. Source-X anchor offset davranışı fixture testleriyle port edilmeli.
5. Target Z, gerçek terrain/surface üzerinden normalize edilmeli.
6. Player placement öncesi preview footprint’i görmeli.
7. Hedef mesafe/LOS/map ve deed erişimi target cevabında tekrar kontrol edilmeli.

Kabul kriterleri:

- Preview ile görülen konum serverda oluşan konumla birebir aynı olmalı.
- Küçük ev, keep, castle ve dört ship yönü ayrı doğrulanmalı.
- Oyuncu cursor açıkken deed’i takas ederse placement iptal olmalı.
- Spoof edilmiş uzak koordinat reddedilmeli.

### Aşama 6 — Placement result ve doğrulama paritesi

1. `bool/null` yerine ayrıntılı placement result kullanılmalı.
2. House validation:
   - Map bounds
   - NoBuild
   - Source-X block radius
   - Source-X multi radius
   - Flatness
   - Water/block/climb flags
   - Character
   - Item
   - House/ship overlap
3. Ship validation:
   - Navigable water surface
   - Water Z
   - Static obstruction
   - Dock/bridge ayrımı
   - Başka hull
   - House region
4. Script veto ayrı neden olmalı.
5. Limit kontrolleri ayrı mesaj üretmeli.

Kabul kriterleri:

- Her failure reason için bir test ve oyuncu mesajı olmalı.
- Log multi defname, raw ID, position, map ve ilk başarısız hücreyi göstermeli.
- Geçerli bir alan hiçbir genel/anonim nedenle reddedilmemeli.

### Aşama 7 — Save/load ve migration

1. Structure kind ve raw multi ID açıkça persist edilmeli.
2. Gerçek `WorldSaver -> WorldLoader` roundtrip testi eklenmeli.
3. Owner UUID restore sırasında kullanılmalı.
4. Component linkleri UUID ile onarılabilmeli.
5. Eski house/ship tag’li normal item’lar migrate edilmeli.
6. Eksik dynamic fixture’lar yeniden üretilebilmeli.
7. Aynı save ikinci kez yüklenince duplicate component oluşmamalı.
8. Custom design revision ve virtual geometry korunmalı.

Kabul kriterleri:

- House/ship restart sonrası registry, region ve ownership’e geri dönmeli.
- Anahtarlar kapı/ship ile eşleşmeye devam etmeli.
- Hold içeriği ve deck eşyaları korunmalı.
- Dry-dock deed aynı ship UUID ve raw ID ile yeniden açılmalı.
- ID `0` gemi roundtrip’i başarılı olmalı.

### Aşama 8 — House gameplay tamamlama

1. Public/private/guild dönüşüm akışı eklenmeli.
2. Access-only yönetimi gump’a bağlanmalı.
3. Vendor ekleme/çıkarma ve limitleri bağlanmalı.
4. `MAXHOUSESGUILD` config sözleşmesi netleştirilmeli.
5. Owner region’a gerçekten girdiğinde decay refresh edilmeli.
6. Decay periyodu config/script sözleşmesine bağlanmalı.
7. Script BaseStorage/BaseVendors uygulanmalı.
8. House sign/door management gerçek component üzerinden çalışmalı.

### Aşama 9 — Ship gameplay tamamlama

1. Script ship speed değerleri uygulanmalı.
2. High Seas access component semantiği uygulanmalı.
3. Deck membership gerçek geometri/surface üzerinden hesaplanmalı.
4. `0xF6` tam component/yolcu/eşya listesiyle üretilmeli.
5. Eski client için delete/recreate veya uygun hareket fallback’i belirlenmeli.
6. Tillerman speech, wheel pilot ve key/plank erişimi birlikte test edilmeli.
7. Dönüş sırasında fixture ve deck objelerinin koordinatları doğrulanmalı.

### Aşama 10 — Uyumluluk test matrisi

Kod değişiklikleri ancak aşağıdaki test paketiyle tamamlanmış sayılmalıdır:

- Minimal Original 12-byte multi fixture
- Minimal High Seas 16-byte multi fixture
- Mevcut `C:\sphereNetServer\mul` üzerinde opt-in yerel doğrulama
- Bütün script house/ship deed tanımlarını tarayan consistency testi
- Bütün MULTIDEF component referanslarını çözen test
- Klasik house placement
- Custom foundation placement ve edit
- Dört ship yönü
- Raw ID `0` ship
- Coast/dock/water/static senaryoları
- NoBuild ve wilderness senaryoları
- House/ship overlap
- Player/account/guild limitleri
- Cursor sırasında deed transferi
- Save/load/restart
- Redeed/dry-dock
- House component cleanup
- Ship cargo moving crate
- Client art ID packet doğrulaması
- `0x99` preview packet doğrulaması
- `0xF6` component listesi doğrulaması

## Önerilen gerçekleştirme sırası

En güvenli sıra:

1. **12/16-byte multi format desteği ve fail-fast validation**
2. **Kanonik raw/client multi ID modeli**
3. **Gerçek deed inheritance ve MULTIDEF metadata loader**
4. **Geometry/dynamic fixture ayrımı**
5. **Placement preview, koordinat ve result nedenleri**
6. **House/ship gerçek save-load migration**
7. **House gameplay eksikleri**
8. **Ship hareket/paket eksikleri**
9. **Bütün format ve multi tiplerini kapsayan test matrisi**

İlk üç aşama tamamlanmadan yalnız placement kontrolünü gevşetmek doğru değildir. Hatalı `MaxY=16092` gibi verilerle sınır kontrolünü atlatmak, on binlerce hücrelik sahte region/component üretimine ve world state bozulmasına neden olabilir.

## Nihai değerlendirme

Mevcut hata basitçe “alan uygun değil” problemi değildir. Kullanılan modern `multi.mul` dosyasının kayıt formatı yanlış okunduğu için house ve ship tanımları bozulmaktadır. Bu, mevcut klasörde ölçülerek doğrulanmıştır.

Sistemin yalnız bu client verisinde değil bütün desteklenen sürümlerde çalışması için çözüm:

- Sadece 16-byte’a geçmek değil,
- 12 ve 16-byte formatı güvenli biçimde ayırt etmek,
- Script MULTIDEF metadata’sını binary geometriyle birleştirmek,
- Raw multi ID ile client art ID’yi ayırmak,
- Gerçek fixture’ları doğru üretmek,
- Placement ve persistence akışını uçtan uca test etmek

olmalıdır.
