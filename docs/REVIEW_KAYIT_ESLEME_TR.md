# Review kayıt eşlemesi

PLAN-005: *"Review 01–13J kayıtlarını mevcut düzeltme commit'leriyle eşleştir.
Son kullanıcı güncellemeleri uygulanmışsa önce yeniden üret; kapanmış sorunu
tekrar uygulama."*

`docs/reviews/` altında **106 bölüm** var (01A'dan 13J'ye) ve içlerinde
**347 bulgu** kayıtlı. Her biri referans kaynağına, SphereNet kaynağına ve bir
tekrar üretim deneyine bağlı. Klasör depoya girmiyor (`.gitignore`), o yüzden
kayıt ancak takip planına geçtiği kadar kalıcı.

---

## Sorun — defteri tutmayan 41 bulgu

Bulgular dört farklı başlık biçimiyle yazılmış:

| Biçim | Örnek | Bölüm |
|---|---|---|
| Kimlikli | `## SX-03A-01 — başlık` | 01A–06C |
| Bölüm-kimlikli | `## 06G-01 — P2: başlık` | 06D–12D |
| Numaralı | `## 4. P2 — başlık` | 12E–13J |
| Yalnız öncelikli | `## P2 — başlık` | 12Z |

Takip planı (`INCELEME_DOGRULAMA_PLANI_TR.md`) hepsini `SX-<bölüm>-<nn>`
biçimine normalleştiriyor. Üçüncü ve dördüncü biçimde yazılan bölümlerde
**41 bulgunun hiçbir onay kutusu satırı yoktu** — ne açık ne kapalı, sadece
görünmez. Takip planından çalışan biri onları hiç görmezdi; review'lardan
çalışan biri kapanmış bir sorunu yeniden uygulayabilirdi. PLAN-005'in
"kapanmış sorunu tekrar uygulama" cümlesi tam olarak bu riski işaret ediyor.

Bunların **41'i de kapalı çıktı.** Hiçbiri açık iş değildi; eksik olan defter
kaydıydı.

---

## Eşleme

41 maddenin 39'u, adı bölüm aralığını taşıyan parite test sınıflarından birine
düşüyor:

| Test sınıfı | Bölümler | Kapsadığı bulgu |
|---|---|---:|
| `SpawnChampionParity12EITests` | 12E–12I | 19 |
| `SpawnParity12JMTests` | 12J–12M | 13 |
| `SpawnParity12NPTests` | 12N–12P | 7 |

Eşleme test **adından** yapıldı, tahminle değil; test adları bulgunun cümlesini
neredeyse birebir taşıyor:

| Bulgu | Kapsayan test |
|---|---|
| 12H-4 — spawn'da STOP komutu uygulanmıyor | `AnItemSpawnerCanBeStopped` |
| 12I-2 — kap içindeki spawn dünyada üretim yapıyor | `ASpawnerInsideABagProducesNothing` |
| 12J-4 — sayısal spawn kimliği 16 bite kesiliyor | `ANumericSpawnIdKeepsItsFullResourceIndex` |
| 12L-2 — ADDOBJ iki üyelik bırakıyor | `HandingACreatureToASecondSpawnerReleasesItFromTheFirst` |
| 12O-5 — canlı ADDOBJ tetikleyiciyi çağırmıyor | `EnrollingAnExistingCreatureFiresTheMembershipEvent` |

Kalan iki madde teste değil başka kanıta bağlanıyor:

- **12J-2** (host köprüsünde PreSpawn tür değişikliği geri kopyalanmıyor) —
  `Program.EngineWiring.cs`'teki köprü artık `ARGN1` sonucunu özgün args'a geri
  taşıyor ve yorumu referansı adıyla anıyor (`CCSpawn.cpp:310/387`). Adanmış bir
  test adı yok; dolaylı kapsanıyor.
- **12Y-5** — bulgu değil. Raporun kendisi *"bu bölüm yeni hata sayılmadı"*
  diyor; 12X-3'ün yükleme yolundaki kapsamı. Çıkarıcı onu başlık biçiminden
  ötürü bulgu sandı.

---

## Sonuç

| Ölçüt | Değer |
|---|---:|
| Bölüm | 106 |
| Bulgu | 347 |
| Takip planında onay kutusu olan | **347** |
| Açık işaretli | **0** |
| Bu dalgada deftere geçen | 41 |

Öncelik dağılımı: 16 P1, 281 P2, 28 P3, 22 önceliksiz.

**Yeni üretim kodu yazılmadı.** PLAN-005 bir muhasebe işiydi ve muhasebe işi
olarak kapandı; kapanmış 41 sorunun hiçbiri yeniden uygulanmadı.

---

## Koruma

`ReviewRecordGuardrailTests`:

1. `docs/reviews/` içindeki her bulgu takip planında bir onay kutusu satırına
   sahip — dört başlık biçimi de tanınıyor, kimlik `SX-<bölüm>-<nn>`'ye
   normalleştiriliyor. Yeni bir review bölümü yazılıp deftere geçmezse test
   kırmızıya döner.
2. Takip planında açık (`- [ ]`) bırakılmış bir review bulgusu varsa adıyla
   bildiriliyor. Bugün sıfır; açık bir madde hata değil, ama sessiz kalmamalı.
3. Bölüm sayısı ve bulgu sayısı bu belgeyle uyuşuyor.

`docs/reviews/` depoya girmediği için üçü de klasör yokken temiz atlıyor
(bkz. [veri kapıları](VERI_KAPILARI_TR.md), kaynak adı `review corpus`).
