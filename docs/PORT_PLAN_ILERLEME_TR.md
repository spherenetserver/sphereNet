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
| Son commit | PLAN-106 kapanışı (bu oturum) |
| Tam test | 3.266 başarılı / 0 başarısız |
| Sıradaki iş | **İŞ-2b: multi TYPE / sahipsiz gemi** (sonra İŞ-3: Save→Load→Save) |

## Çalışma sırası

Sıra, [port durumu doğrulama planından](reviews/PORT_DURUM_DOGRULAMA_VE_BUYUK_PLAN_TR.md)
(yerel) türetildi; oradaki PLAN-xxx numaraları parantezde. Öncelik ölçütü: önce
kanıtlanmış veri kaybı riski, sonra script sözleşmesi, sonra kapsam genişletme.

- [x] **İŞ-1 — 12W: TIMERF deseni, süre ifadesi ve sorgu** (PLAN-205 kesiti)
  Takip planındaki üç açık madde. Küçük, kodu hazır, referans sözleşmesi net.
  Kabul: `TIMERF STOP` / `ISTIMERF` tam komut deseniyle eşleşir (`*`, `?` dahil,
  argümanlar eşleştirmeye katılır); süre ifadesi çarpma/parantez/boşluk tüketir;
  `ISTIMERF` ilk eşleşmeyi döndürür ve sıfırı geçerli sonuç sayar.

- [x] **İŞ-2 — 56T'nin eşlenmeyen kayıt anahtarları** (PLAN-106) — **KAPANDI**
  Altı dilim: (1) shard'ın skill adları, (2) kaydın türü tanımdan — harita pini/kitap
  sayfası, (3) yapının bölgesi (REGION.TAG.\*), (4) klasik taşın loncası,
  (5) klasik geminin ambarı/iskeleleri, (6) 0.56'nın öldürme sayaçları.
  **Paketsiz ölçümde 17 → doğru ölçümde 0.** 56T dökümü artık hiçbir anahtarı park
  etmeden yükleniyor; ölçüm paket + sunucu kablolamasıyla yapılıyor ve testte kilitli.

- [ ] **İŞ-2b — multi'nin TYPE'ı ve sahipsiz gemi** (İŞ-2'de ortaya çıktı)
  Multi'nin TYPE'ı `[MULTIDEF]`'ten gelir, motor onu multi kayıt defterinde tutar;
  import edilen tekne `ItemType.Ship` değil, bu yüzden `ShipEngine`in tür kapısından
  geçmiyor. Ayrıca aynı kapı OWNER arıyor ve 56T'nin 8 gemisinin hiçbirinde OWNER yok.
  Sonuç: import edilen gemiler hiç kayıt olmuyor (bölge yok, dümen yok, iskele yok).
  Veri artık korunuyor — eksik olan gemiyi canlandırmak.

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

- **İŞ-2 KAPANDI (PLAN-106)** — 2026-09-07. Son dilim: 0.56'nın KILLSPLAYER'ı
  referansın tek sayacına (KILLS) çevriliyor, KILLSNPC tag olarak korunuyor
  (referansta karşılığı yok). 56T dökümünde eşlenmeyen anahtar **0**; 834 karakter
  cinayet sayısını geri aldı. Tam suite 3.266.
- **İŞ-2 beşinci dilim (PLAN-106)** — 2026-09-07. Klasik geminin HATCH/PLANK
  satırları okunuyor ve gemi katmanına bileşen olarak kaydediliyor; klasik "temizlenmiş
  uid" işaretçisi için `Serial.NamesAnObject` eklendi. Ölçüm 4 → 2. Test:
  `LegacySaveKeyParityTests` +8; tam suite 3.265.
- **İŞ-2 dördüncü dilim (PLAN-106)** — 2026-09-07. Klasik lonca/şehir taşının
  ALIGN/ABBREV/CHARTER/MEMBER satırları motorun GUILD.* biçimine çevriliyor; gerçek
  veride 10 lonca geri geldi (en büyüğü 13 üye). Taş kendi adını veriyor. Ölçüm
  8 → 4. Test: `LegacySaveKeyParityTests` +4; tam suite 3.257.
- **İŞ-2 üçüncü dilim (PLAN-106)** — 2026-09-07. Multi kaydındaki
  `REGION.TAG.<ad>` satırları okunuyor, geri yazılıyor ve ev/gemi bölgesi
  gerçekleşirken bölgeye kopyalanıyor (93+1 kayıt). Okuma bilinçli olarak eşyada
  yakalanmadı — bölgeye gidiyor. Ölçüm 10 → 8. Test: `LegacySaveKeyParityTests` +3;
  tam suite 3.253.
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
