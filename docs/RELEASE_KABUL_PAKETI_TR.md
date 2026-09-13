# Release kabul paketi

PLAN-705'in istediği altı başlık. **Bu belge bir onay değil, bir durum
bildirimidir:** koşulmuş olanı koşulmuş, koşulmamışı koşulmamış olarak yazar.
Dalganın kabul ölçütü de bunu söylüyor — "ölçülmeyen latans için rastgele
milisaniye hedefi konmaz".

Sayıların bir kısmı `ReleaseAcceptanceGuardrailTests` tarafından sabitlenmiştir;
belge kaynaktan sessizce ayrışamaz.

---

## 1. Tam test

| Alan | Değer |
|---|---|
| Test sayısı | **3.625 başarılı / 0 başarısız** |
| Test dosyası | 450 |
| Koşu sayısı | Her dalga sonunda üç arka arkaya tam koşu |
| Komut | `dotnet test src/SphereNet.Tests/SphereNet.Tests.csproj` |

Gerçek veriye bağlı testler (56T save, canlı script paketi, referans tablolar)
kaynak yoksa **temiz atlar** — CI'da yeşil kalır, yerelde ölçer.

---

## 2. Veri manifesti

Çalışan shard'ın ihtiyaç duyduğu veri (`C:\sphereNetServer`):

| Bileşen | Ölçüm | Not |
|---|---|---|
| `mul/` | 29 dosya | map0-3, statics0/1 + staidx0/1, tiledata, multi, art, hues, anim… |
| `scripts/` | 550 `.scp` | Klasik-era paket; kendi lonca/kasaba diyalog sistemini taşıyor |
| `save/` | 5 dosya | `sphereworld`, `spherechars`, `spheredata`, `spheremultis`, `spherestatics` |
| `sphere.ini` | 1 | `OPTIONFLAGS=0x2080`, `MAGICFLAGS=0`, `EQUIPPEDCAST=0` |
| İstemci | 7.0.20 klasik | KR/Enhanced desteklenmiyor, hedeflenmiyor |

**Erişilemeyen veri:** mapdiff/stadiff dosyaları yok (`USEMAPDIFFS` tüketicisiz,
bkz. [İŞ-43]). AOS+ item property'lerinden paket yalnızca 2'sini kullanıyor
(bkz. [AOS property matrisi](AOS_PROPERTY_MATRISI_TR.md)).

---

## 3. Açık sapmalar

Referanstan **bilinçli** olarak ayrıldığımız, her biri gerekçesiyle kayıtlı
**13 nokta** var. Tam metinleri
[inceleme planında](INCELEME_DOGRULAMA_PLANI_TR.md) ilgili işin
"Kayıtlı sapma" başlığı altında; özet:

| İş | Sapma |
|---|---|
| İŞ-19 | `@CombatStart` hedef değişiminde istemci tarafında ateşleniyor |
| İŞ-20 | `@HitCheck` ARGN2 (hasar türü) geri okunmuyor |
| İŞ-21 | ARGN2 yazılabiliyor ama yazılan tür darbeye taşınmıyor |
| İŞ-22 | `SPELLCHANNELING` eşya property'si yok |
| İŞ-23 | `[SPELL] @Select` argüman sözleşmesi eksik |
| İŞ-24 | `MAGICF_NOFIELDSOVERWALLS` okunmuyor |
| İŞ-28 | `@FollowersUpdate` tetikleyicisi yok (pakette kullanım yok) |
| İŞ-30 | Ölen NPC binici koşulsuz iniyor (pakette binekli NPC yok) |
| İŞ-31 | `ObjAttributes` yüksek bitleri referansla ayrışıyor — **save uyumu** gereği değiştirilmedi |
| İŞ-35 | `@HouseDesignCommitItem` yazılmadı (referans paket yalnızca yorum satırı) |
| İŞ-36 | `OF_MapBoundarySailing` tüketicisiz; kaydırma aritmetiği doğrulanamadı |
| İŞ-37 | Lonca reddi sessiz (eşya-konuşma kanalı yok) |
| İŞ-38 | `SPEECHFILTER` ve `SETMASTER` yok (iki pakette de sıfır kullanım) |

Ortak ölçüt: bir sapma ya **paket tarafında tüketicisi olmadığı** için, ya
**save/veri uyumunu bozacağı** için, ya da **referansta da uygulanmadığı** için
bilinçlidir. Hiçbiri "unutuldu" değil.

---

## 4. Bilinen sorunlar

| Sorun | Durum |
|---|---|
| **Tekrar üretilemeyen tek test başarısızlığı** | İŞ-43'ten sonraki 12 tam koşunun birinde görüldü (3.601/3.602); adı yakalanamadı, sonraki 15+ koşuda tekrar etmedi. Şüphe: yeni testlerin geçici klasör kullanımında Windows dosya kilidi. **Açık.** |

Bunun dışında kritik açık bug kaydı yok.

---

## 5. Gerçek istemci smoke

**KOŞULMADI**. Bu turda gerçek bir 7.0.20 istemcisiyle elle oturum açılmadı.

Yerine ne var:
- Login zinciri uçtan uca entegrasyon testiyle sürülüyor (seed → login → server
  list → relay → game login → char list → dünyaya giriş).
- TCP parçalanma/birleşme, bayt bayt teslim ve yeniden bağlanma kapsandı
  ([İŞ-42]).
- Paket biçimleri opcode/alt-komut matrisiyle referansa karşı ölçüldü
  ([paket matrisi](PAKET_MATRISI_TR.md)).

Bunlar bir istemci smoke'unun **yerine geçmez**; sürüm kabulü için gerçek
istemciyle bir oturum gereklidir.

---

## 6. Soak ve restore çıktıları

### Soak — **KOŞULMADI**

PLAN-702'nin 2/8/24 saatlik koşuları çalıştırılmadı. Planın kendi notu da öyle
diyor ("süreler kabul planı önerisidir; bu tur çalıştırılmadı").

Hazır olan: `LoadProfile.Capture` bir soak koşusunun karşılaştıracağı 23 alanı
tek kayıtta alıyor (oyuncu/NPC/spawner, hareket, savaş, script geri çağrımı,
tick p50/p95/p99, kayıt süresi, bellek, GC, kuyruk, açıklanamayan nesne).
**Eşik konmadı** — eşikler hedef donanımdaki ilk baseline'dan çıkacak.

### Restore — **KOŞULDU**

| Tatbikat | Sonuç |
|---|---|
| Bozuk güncel kayıt | Önceki kuşağa dönüyor |
| Eksik shard | Geri dönüyor; yedek yoksa kısmi yüklemek yerine hata veriyor |
| Disk yazma hatası | Önceki kuşak sağlam kalıyor |
| Yarım işlenmiş (torn) commit | Yakalanıyor |
| Hepsi bozuk | Açık hata; sessizce boş dünya başlatmıyor |
| **Öldürülmüş kayıt** (İŞ-45) | `.tmp` enkazı yok sayılıyor; önceki kuşak yükleniyor |
| Yeniden başlatma dizisi | kaydet → başlat → kaydet → başlat → en yeni kuşağı kaybet → başlat |

---

## Kabul özeti

| Ölçüt | Durum |
|---|---|
| Açıklanmayan sürekli bellek/nesne büyümesi yok | **Ölçülmedi** — soak koşulmadı; ölçüm zemini hazır |
| Kayıt kurtarma adımları gerçekten denenmiş | **Evet** |
| Kritik açık bug yok | **Evet** (bir açık kalem, bkz. §4) |
| Ölçülmeyen latans için hedef konmamış | **Evet** — hiçbir eşik yazılmadı |

**Sürüm kabulü için eksik iki adım: gerçek istemci smoke ve soak koşusu.**

---

## Kapsam uyarısı

Bu paket **büyük planın Dalga 4-7**'sini (pet/ölüm, housing/gemi/sosyal, modern
özellikler, operasyon) kapsıyor. **Dalga 0-3 henüz açılmadı** ve 23 maddesi
açık: doğru başlangıç noktası/ölçüm, nesne ve kayıt bütünlüğü, script
yürütme/trigger/fabrika sırası, config ve temel script sorguları.

Yani bu belge "her şey hazır" demiyor; **ölçülen kısmın durumunu** bildiriyor.
