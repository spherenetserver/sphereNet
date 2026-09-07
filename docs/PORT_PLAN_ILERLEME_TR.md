# Port planı — çalışma sırası ve kaldığımız yer

Bu dosya, birden çok oturuma yayılan port/parite çalışmasının **devam noktasıdır**.
Yeni bir oturum buradan başlar: "Sıradaki iş" bölümünü oku, o maddeyi yap, bitince
buradaki kutuyu işaretle ve "Son durum" satırını güncelle.

- Bulgu ve kanıt raporları: `docs/reviews/` (Git ignore altında, yerel).
- Kapanan bulguların kalıcı kaydı: [INCELEME_DOGRULAMA_PLANI_TR.md](INCELEME_DOGRULAMA_PLANI_TR.md).
- Kullanıcıya dönük değişiklik kaydı: `CHANGELOG-EN.txt` / `CHANGELOG-TR.txt`.
- Bu dosya yalnızca **sıra ve durum** tutar; bulgu metnini buraya kopyalama.

## Son durum

| Alan | Değer |
|---|---|
| Son güncelleme | 2026-09-07 |
| Son commit | PLAN-106 ikinci dilim (bu oturum) |
| Tam test | 3.250 başarılı / 0 başarısız |
| Sıradaki iş | **İŞ-2 devamı: REGION.TAG.\* (94) — tek kök** |

## Çalışma sırası

Sıra, [port durumu doğrulama planından](reviews/PORT_DURUM_DOGRULAMA_VE_BUYUK_PLAN_TR.md)
(yerel) türetildi; oradaki PLAN-xxx numaraları parantezde. Öncelik ölçütü: önce
kanıtlanmış veri kaybı riski, sonra script sözleşmesi, sonra kapsam genişletme.

- [x] **İŞ-1 — 12W: TIMERF deseni, süre ifadesi ve sorgu** (PLAN-205 kesiti)
  Takip planındaki üç açık madde. Küçük, kodu hazır, referans sözleşmesi net.
  Kabul: `TIMERF STOP` / `ISTIMERF` tam komut deseniyle eşleşir (`*`, `?` dahil,
  argümanlar eşleştirmeye katılır); süre ifadesi çarpma/parantez/boşluk tüketir;
  `ISTIMERF` ilk eşleşmeyi döndürür ve sıfırı geçerli sonuç sayar.

- [ ] **İŞ-2 — 56T'nin eşlenmeyen kayıt anahtarları** (PLAN-106) — **KISMEN**
  Kapanan dilimler: (1) shard'ın kendi skill adları, (2) kaydın türünün tanımdan
  gelmesi — harita pinleri ve kitap sayfaları. Ölçüm de düzeltildi: paket **ve**
  sunucu kablolaması yüklüyken çalışan geçiş. Paketsiz ölçümde 17, doğru ölçümde
  **10** tür anahtar kalıyor.

  Kalanların sınıflandırma taslağı — **hepsi doğrulanmamış hipotez**:

  | Anahtar | Adet | Hipotez | Nereye bakmalı |
  |---|---:|---|---|
  | KILLSPLAYER | 897 | 56x'in ayrık öldürme sayacı; motorda tek `KILLS` var. Source-X'te bu ad YOK — 0.56 dönemi alanı. Tasarım kararı ister. | `Character.Kills`, `WorldSaver:907` |
  | KILLSNPC | 286 | aynı ailenin NPC yarısı | aynı |
  | REGION.TAG.owner | 93 | bölge tag'i; nesne kaydında region alt-bloğu | `WorldLoader` region dalı |
  | ALIGN / MEMBER / ABBREV / CHARTER0 | 28 | lonca taşı alanları (hizalanma, üye, kısaltma, ferman) | guild stone kalıcılığı |
  | HATCH / PLANK | 9 | gemi bileşen bağlantıları | `ShipEngine`, multi kaydı |
  | REGION.TAG.hp_bar | 1 | bölge tag'i (yukarıdakiyle aynı kök) | aynı |

  Sıradaki adım: REGION.TAG.* (94, tek kök) → lonca alanları (28, tek kök) →
  gemi HATCH/PLANK (9) → en sonda KILLSPLAYER/KILLSNPC (1183; `KILLS`e mi toplanacak,
  ayrı alan mı — kullanıcı kararı isteyebilir).

  Ayrıca bu dilimden çıkan yan iş: `Item` içinde kalan ham `_type` kapıları
  (yığın/hafıza/multi-custom/statik blok) aynı sınıf hataya açık; davranış yolları
  oldukları için bilinçli olarak ertelendi.

- [ ] **İŞ-3 — Save→Load→Save alan bazında eşitlik** (PLAN-107)
  Temel stat, miktar, owner/parent, spawn üyeliği, timer ve vendor içeriği için
  alan bazında karşılaştırma testi. 13J'nin temel/etkin havuz hatası tam da bu
  sınıftı; invariant testi olmadan aynı sınıf yine kaçar.
  Kabul: iki tur kayıt sonrası hiçbir alan sessizce değişmiyor; değişenler
  belgeli.

- [ ] **İŞ-4 — Tek nesne oluşturma/kopyalama sözleşmesi tablosu** (PLAN-101)
  NEWITEM, NEWNPC, NEWDUPE, karakter DUPE, spawn, template, vendor ve stack
  bölme girişlerinin ortak/farklı davranışını tabloya dök; farkları ya kapat ya
  da bilinçli sapma olarak kaydet. 13G-13J bu tablonun yarısını zaten yazdı.

- [ ] **İŞ-5 — Gerçek ini fark listesi** (PLAN-301/302)
  Canlı `sphere.ini` ile desteklenen anahtar manifestini karşılaştır; etkisiz
  kalan oyun anahtarlarını çıkar. İlk paket: hareket/ağırlık/stamina.

## Yapıldı

Bu bölüm yalnızca bu plandaki işlerin kapanışını listeler; bulgu ayrıntısı takip
planındadır.

- **İŞ-2 ikinci dilim (PLAN-106)** — 2026-09-07. Kitap/harita/gemi property
  kapıları ham `_type` yerine etkin türe (`Item.EffectiveType`) bakıyor; klasik
  kayıtta TYPE satırı olmadığı için 59 harita pini ve 18 kitap sayfası okunmuyordu.
  Gerçek veri geçişi sunucu kablolamasını aynalıyor. Ölçüm 15 → 10. Test:
  `LegacySaveKeyParityTests` +3; tam suite 3.250.
- **İŞ-2 ilk dilim (PLAN-106)** — 2026-09-07. Shard'ın kendi skill adları
  (`[SKILL n] KEY=`) yükleme ve script yollarında çözülüyor; üç ayrı isim tablosu
  `SkillNames`te birleşti; `Farming` adını kapan NEWBIE kaynağı yüzünden defname
  tablosu yerine skill blokları kendi adlarıyla indeksleniyor. Gerçek veri ölçümüne
  paketli geçiş eklendi (17 → 15 anahtar). Test: `LegacySaveKeyParityTests` (5) +
  paketli 56T geçişi; tam suite 3.247.
- **İŞ-1 (12W)** — 2026-09-07. `SpherePattern` (Str_Match portu) ve
  `ScriptNumber.TryEvaluatePrefix` eklendi; `ClearTimerF`/`GetTimerFRemaining`
  komutun tamamıyla eşleşiyor, `ISTIMERF` ilk eşleşmeyi dönüyor, TIMERF süresi
  ifade olarak tüketiliyor. Test: `DelayedCallParity12WTests` (40); tam suite
  3.241. Düzeltmeler geçici geri alınıp 12 testin eski davranışı yakaladığı
  doğrulandı. Ayrıntı takip planında 12W bölümünde.

## Oturum notları

- 2026-09-07: Plan kuruldu. 12W öncesi durum: takip planı 352 kapalı / 3 açık,
  tam test 3.201. Port durumu doğrulama planının Çevrim 1'i (PLAN-001..005'in
  kayıt eşleme kısmı) `3cd431b` ve `8611064` ile fiilen kapandı; PLAN-102/103 ve
  PLAN-202 ile PLAN-203'ün yarısı da 13I/13J dalgasında kapandı.
