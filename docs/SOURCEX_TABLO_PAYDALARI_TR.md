# Source-X tablo paydaları

PLAN-001: *"Source-X tablo kapsamını ve alias eşlemelerini JSON/CSV'ye çıkar;
hangi tabloların hangi paydada yer aldığını açıkla."*

Bu depodaki kapsama iddiaları "X / Y" biçiminde yazılıyor. Y neredeyse her zaman
bir Source-X tablosunun boyutu — bir kere elle yazılmış, bir daha kontrol
edilmemiş. **İkisi yeniden üretilemiyor.** Bu belge paydaların nereden geldiğini
söyler; sayıların kendisi
[`docs/data/sourcex_tables.csv`](data/sourcex_tables.csv) dosyasından gelir ve
`SourceXTableInventoryGuardrailTests` her koşuda referanstan yeniden çıkarıp
dosyayla karşılaştırır.

Dalganın kabul ölçütü buydu: *"başka bir makinede aynı commit/veri manifestiyle
aynı paydalar elde edilir."* Bu ancak payda **türetilirse** doğru olur, elde
tutulursa değil.

---

## Çıkarım

`oldSphere/Source-X-full/src` içinde **98 tablo**, toplam **3570 giriş**.

İki kaynak biçimi var:

| Biçim | Nerede | Örnek |
|---|---|---|
| `.tbl` makro dosyası | `src/tables/*.tbl` | `ADD(AC, "AC")` |
| Satır içi `lpctstr` dizisi | `.cpp` içinde | `lpctstr const CChar::sm_szTrigName[] = { "@Attack", ... }` |

`.tbl` dosyaları C++ tarafında `#define ADD(a,b) b,` ile açılıyor, yani bir
`.tbl` dosyasının sahibi onu `#include` eden tablodur. CSV her satırda hem
dosyayı (`table`) hem sahibini (`owner`) taşır — bir script `CChar_props.tbl`'e
değil `CChar::sm_szLoadKeys`'e çarpar.

`ADDPROP` üçüncü bir alan taşıyor: property'yi açan genişleme. Referansın era
kapısını koda değil veriye yazdığı tek yer burası, o yüzden CSV'de `era`
sütunu olarak duruyor (219 kapılı property: 101 `RDS_AOS`, 62 `RDS_PRET2A`,
45 `RDS_SA`, 7 `RDS_TOL`, 2 `RDS_ML`, 2 `RDS_HS`).

### Türe göre

| Tür | Giriş | Benzersiz anahtar |
|---|---:|---:|
| `attributes` (props `.tbl`) | 1927 | 1664 |
| `triggers` | 532 | 500 |
| `props` (satır içi LoadKeys) | 381 | 321 |
| `functions` (verb `.tbl`) | 318 | 293 |
| `verbs` (satır içi VerbKeys) | 148 | 136 |
| `other` | 139 | 138 |
| `class` (classnames) | 77 | 77 |
| `refs` | 48 | 44 |

`attributes` sayısının 1165'i `defmessages.tbl` — yerelleştirme metinleri.
Script yüzeyi paydası hesaplanırken ayrı tutulmalı.

---

## Neden toplama yapılamaz

**412 anahtar birden fazla tabloda tanımlı.** Biri on ayrı tabloda:

| Anahtar | Kaç tabloda |
|---|---:|
| `NAME` | 10 |
| `DEFNAME` | 9 |
| `ACCOUNT` | 8 |

Dağılım: 328 anahtar 2 tabloda, 55'i 3, 18'i 4, kalanı daha fazlasında.

Bu Source-X'te bir hata değil — arama zinciri `CChar → CObjBase → CBaseBaseDef`
diye iniyor ve her katman kendi tablosunda aynı adı taşıyabiliyor. Ama payda
hesabı için sonucu şu: **tabloları toplayan bir payda, aynı anahtarı birden çok
kez sayar.** İki sayı da doğrudur, farklı şeylerin sayısıdırlar; bir kapsama
iddiası hangisini kastettiğini söylemek zorundadır.

---

## Ölçülen paydalar ve rapordaki karşılıkları

### Trigger yüzeyi — rapor **248** diyor

Üç farklı doğru sayı var:

| Payda | Değer | Ne demek |
|---|---:|---|
| `triggers.tbl` (`kOrderedTrigsNames`) | **248** | Sıralı global ad listesi |
| Sınıf tablolarının birleşimi | **252** | `CChar` 191, `CItem` 59, skill 12, spell 10, region 6, region-resource 4, webpage 2 |
| İkisinin birleşimi | **253** | Ulaşılabilir tüm trigger adları |

Listeler birbirini kapsamıyor. Beş trigger yalnızca sınıf tablolarında:
`CHARCONTEXTMENUREQUEST`, `CHARCONTEXTMENUSELECT`, `ITEMREDEED`,
`ITEMREGIONENTER`, `ITEMREGIONLEAVE`. Bir trigger yalnızca global listede:
`ITEMFIRE`.

Yani rapor 248'i kullanırken iki gerçek sayının **küçüğüne** karşı ölçüyor.
Sapma küçük ama payda keyfi değil, seçilmiş olmalı.

### Verb yüzeyi — rapor **206 / 206** diyor

`CObjBase_functions` 57 + `CChar_functions` 74 + `CItem_functions` 14 +
`CClient_functions` 61 = **206 giriş**. Benzersiz anahtar ise **186** — yirmi
anahtar bu dört tablonun birden fazlasında tanımlı.

206 doğru bir sayı: *tablo girişi* sayısı. 186 da doğru: *ayrı verb* sayısı.
"206/206" ifadesi ikisini birbirinin yerine kullanıyor.

### Property yüzeyi — rapor **470 / 645** diyor

`*_props.tbl` dosyaları **777 giriş / 571 benzersiz anahtar**. **645 bunların
hiçbiri değil** ve türetilebilir bir kuralla da çıkmıyor (bileşen tablolarını
çıkarmak, yerelleştirmeyi çıkarmak, benzersizleştirmek — hiçbiri 645 vermiyor).

Bu sayının kaynağı bulunamadı. Paydası bilinmeyen bir orana pay yazmak
ölçüm değil, o yüzden düzeltmesi PLAN-004'e bırakıldı: yeni payda belli
(777 ya da 571, hangisi kastediliyorsa), pay yeniden ölçülmeli.

### Ini anahtarları — 279

`CServerConfig.cpp` 279 ini anahtarı tanımlıyor. Bu payda ayrı bir belgede
ölçülüyor: [ini anahtar sınıflandırması](INI_ANAHTAR_SINIFLANDIRMASI_TR.md).

---

## CSV biçimi

| Sütun | Anlamı |
|---|---|
| `source` | Referans ağacındaki dosya (`tables/CChar_props.tbl` ya da `game/chars/CChar.cpp`) |
| `table` | Tablo adı — `.tbl` dosya adı ya da C++ dizi adı |
| `owner` | Script'in gerçekten çarptığı C++ tablosu (`.tbl` dosyaları için onu `#include` eden) |
| `prefix` | Enum öneki (`CHC_`, `TRIGGER_`, …), `.tbl` başlığındaki `Prefix:` satırından |
| `kind` | `.tbl` başlığındaki `Set:` satırı, ya da C++ dizi adından türetilir |
| `index` | Tablodaki sıra — trigger sırası anlamlıdır |
| `enum` | Enum sabiti (`.tbl` girişlerinde) |
| `key` | Script'in yazdığı anahtar |
| `era` | `ADDPROP` genişleme kapısı; diğerlerinde boş |

---

## Koruma

`SourceXTableInventoryGuardrailTests` (6):

1. CSV referanstan yeniden çıkarılıyor ve satır satır karşılaştırılıyor — elle
   düzenleme ya da referans güncellemesi kırmızıya döner.
2. Trigger paydasının 248 / 252 / 253 üçlüsü sabit; iki listenin birbirini
   kapsamadığı (`ITEMFIRE` tek yönde, beş ad diğer yönde) ölçülüyor.
3. Verb tablolarının 206 giriş / 186 anahtar ayrımı sabit.
4. Property yüzeyinin 777 / 571 olduğu ve **645 olmadığı** sabit.
5. `ADDPROP` era kapıları sayılıyor.
6. Bu belgenin toplamı CSV ile uyuşuyor.

`oldSphere/` yoksa hepsi temiz atlıyor, CI'da yeşil kalıyor.

---

## Kalan iş

Bu belge **paydaları** ölçüyor, payları değil. Her tablonun kaç anahtarını
karşıladığımız ayrı bir ölçüm ve zaten kısmen var:
`SourceXVerbInventoryGuardrailTests` verb tablolarını, `TriggerCoverageGuardrailTests`
trigger'ları, `AosPropertyCoverageGuardrailTests` bileşen property'lerini
ölçüyor. Rapordaki oranların yeniden hesaplanması PLAN-004'e ait.
