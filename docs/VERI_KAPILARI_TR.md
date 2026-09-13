# Veri kapıları

PLAN-003: *"Early-return testlerini görünür veri-yok durumuna dönüştür;
donanım/veri gereken testlerin çalışıp çalışmadığını TRX yanında raporla."*

Bu paketin bir bölümü gerçek veriyi ölçüyor: canlı script paketi, 56T kaydı,
`.mul` tabloları, Source-X referans ağacı. Hiçbiri depoya konamıyor, o yüzden o
testler veri yokken erkenden dönüyordu — ve xUnit 2'de çalışma anında "skip"
yok, dolayısıyla **BAŞARILI** olarak raporlanıyorlardı.

Sonuç: veri olmayan bir makinedeki koşu, her şeyi ölçen bir koşudan ayırt
edilemiyordu. "3636 yeşil" göründüğünden azını söylüyordu.

---

## Ne yapıldı

Yeni bir skip durumu uydurulmadı. Bunun yerine her kapı `Gate` üzerinden geçiyor
ve kararı kaydediyor:

```csharp
if (Gate.Missing(_out, "live script pack", !Directory.Exists(pack))) return;
if (Gate.MissingValue(_out, "config/sphere.ini", ini)) return;
```

Kontrolün şekli değişmedi; değişen, kararın **kaydedilmesi**. Veri yoksa test
çıktısına da bir satır düşüyor:

```
NO-DATA: live script pack - this test measured nothing
```

`MissingValue` ayrı duruyor çünkü `[NotNullWhen(false)]` taşıyor: yoksa null
kontrolünü `Gate`'e taşıyan her test derleyicinin null analizini kaybeder ve
arkasından `!` yazmak gerekirdi.

### Rapor

Her kapı değerlendirmesinde `TestResults/data-gates.md` ve `data-gates.csv`
yeniden yazılıyor — TRX'in yanına, `dotnet test`'in zaten kullandığı klasöre.
`SPHERENET_TEST_REPORT_DIR` ile başka bir yere alınabilir.

Rapor koşu sonunda değil **her çağrıda** yazılıyor. xUnit 2'de assembly
teardown yok, ve yalnızca temiz çıkışta oluşan bir rapor tam da koşu yarıda
öldüğünde eksik olurdu.

Başlık satırı okunması gereken tek sayıyı veriyor:

```
**79 gate(s) evaluated, 1 found no data.**
```

---

## Ölçüm

**82 kapı çağrısı**, 27 dosyada, **20 ayrı kaynak**:

| Kaynak | Kapı sayısı | Ne |
|---|---:|---|
| `live script pack` | 14 | `C:\sphereNetServer\scripts` |
| `external script pack` | 7 | Ortam değişkeniyle verilen paket |
| `Source-X reference tree` | 6 | `oldSphere/Source-X-full/src` |
| `config/sphere.ini` | 5 | Depodaki ini |
| `table export` | 6 | `docs/data/sourcex_tables.csv` |
| `release package` | 4 | `docs/RELEASE_KABUL_PAKETI_TR.md` |
| `mul tables` | 4 | `tiledata.mul` ve komşuları |
| `live shard (scripts + mul + save)` | 4 | Üçü birden gerekiyor |
| `denominator document` | 10 | `docs/SOURCEX_TABLO_PAYDALARI_TR.md` |
| `script pack fixtures` | 3 | `tests/fixtures/scripts` |
| `review corpus` | 3 | `docs/reviews/` (depoya girmiyor) |
| `findings document` | 6 | `docs/INCELEME_DOGRULAMA_PLANI_TR.md` |
| `engine source` | 2 | `src/` taranabiliyor mu |
| `56T scripts and save` | 2 | 56T paketi + kaydı |
| `reference tables` | 1 | `src/tables/*.tbl` |
| `reference tables + live pack` | 1 | İkisi birden |
| `reference tables + modern pack` | 1 | İkisi birden |
| `progress plan` | 1 | `docs/PORT_PLAN_ILERLEME_TR.md` |
| `UOP map files` | 1 | `map0x.mul` / UOP varyantları |
| `56T save` | 1 | Yalnızca kayıt |

Bu makinedeki son koşuda 79 kapı değerlendirildi ve **biri** veri bulamadı:
`StairThrowDiagnosticTests.CompareMap0_vs_Map0x_Terrain_AroundBuilding`,
`UOP map files` yok. Yani bu koşuda bir test ölçüm yapmadan yeşil döndü — ve
artık bunu rapor söylüyor.

Değerlendirme sayısının (79) çağrı sayısından (82) farklı olması normal: bir
`[Theory]` aynı kapıya vaka başına uğrar ve bu tekrarlar tek kayda düşer;
veri yoksa arkadaki kapılara hiç varılmaz.

---

## Neden xUnit'in skip'i kullanılmadı

`Assert.Skip()` xUnit v3'te var; bu paket 2.9.2'de. `SkippableFact` paketi
eklenebilirdi ama üç şeyi getirirdi: yeni bir bağımlılık, 82 çağrı yerine her
gated testin **özniteliğini** değiştirmek, ve TRX'te "Skipped" görünen ama **hangi verinin**
eksik olduğunu söylemeyen bir durum.

Sorulan soru "kaç test atlandı" değil, *"hangi veri yoktu ve bu yüzden ne
ölçülmedi"*. Rapor bunu cevaplıyor; skip durumu cevaplamıyor.

Not: kapılar teste **başarısızlık** yazmıyor. Veri yokluğu bir hata değil, bir
koşu özelliği — CI'da referans ağacı da canlı paket de yok ve orada yeşil kalmak
doğru davranış. Yanlış olan, bunun görünmez olmasıydı.

---

## Koruma

`DataGateGuardrailTests` (4):

1. **Sessiz kapı kalmadı.** Test kaynaklarını tarayıp `Directory.Exists`,
   `File.Exists`, `== null`, `.Count == 0` gibi bir kontrolden çıplak `return;`'e
   giden ve `Gate`'ten geçmeyen her satırı bildiriyor. İki gerçek akış kontrolü
   (`SaveRoundTripParityTests`, `SpellRuneItemGraphicTests`) dosya adıyla birlikte
   istisna listesinde — liste blanket muafiyete dönüşemesin diye.
2. **Kaynak sözlüğü sabit.** 20 ad pinlenmiş; yazım hatası aynı kaynağı iki ayrı
   satır gibi gösterirdi.
3. **Rapor gerçekten yazılıyor.** Kendi kapısını geçtiği için sıradan bağımsız.
4. Bu belge raporda görülecek kaynak adlarını sayıyor.

Geri-alma sondajı: `SpellCoverageGuardrailTests`'teki bir kapı eski sessiz
biçimine döndürüldüğünde tarayıcı onu dosya ve satır numarasıyla yakalıyor.
