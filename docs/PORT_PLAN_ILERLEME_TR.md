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
| Son commit | 12W dalgasi (bu oturum) |
| Tam test | 3.241 başarılı / 0 başarısız |
| Sıradaki iş | **İŞ-2: 56T eşlenmeyen kayıt anahtarları (PLAN-106)** |

## Çalışma sırası

Sıra, [port durumu doğrulama planından](reviews/PORT_DURUM_DOGRULAMA_VE_BUYUK_PLAN_TR.md)
(yerel) türetildi; oradaki PLAN-xxx numaraları parantezde. Öncelik ölçütü: önce
kanıtlanmış veri kaybı riski, sonra script sözleşmesi, sonra kapsam genişletme.

- [x] **İŞ-1 — 12W: TIMERF deseni, süre ifadesi ve sorgu** (PLAN-205 kesiti)
  Takip planındaki üç açık madde. Küçük, kodu hazır, referans sözleşmesi net.
  Kabul: `TIMERF STOP` / `ISTIMERF` tam komut deseniyle eşleşir (`*`, `?` dahil,
  argümanlar eşleştirmeye katılır); süre ifadesi çarpma/parantez/boşluk tüketir;
  `ISTIMERF` ilk eşleşmeyi döndürür ve sıfırı geçerli sonuç sayar.

- [ ] **İŞ-2 — 56T'nin eşlenmeyen kayıt anahtarları** (PLAN-106)
  Gerçek veri yüklemesinde raporlanan 17 tür eşlenmeyen anahtarı sınıflandır:
  `motor eksiği / bilinçli desteklenmiyor / script alanı / bozuk kayıt`. Her SAVE.*
  kaydını otomatik motor hatası sayma. Tek gerçek **P0 riski** burada: sessiz veri
  kaybı. Gerçek 56T verisi gerekiyor (`C:\mortechUO\old\save\`).
  Kabul: 17 anahtarın tamamı sınıflandırılmış; motor eksiği çıkanlar ayrı iş
  maddesine dönüşmüş; bilinçli olanlar sapma kaydına yazılmış.

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
