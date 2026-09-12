# AOS+ property matrisi — hangi property'nin gerçekten tüketicisi var

PLAN-602: *"AOS/SE/ML/SA/TOL property'lerini kullanılan içerik ve client era ile
eşleştir. Bir property tooltip'te görünüyor diye combat etkisi tamam sayılmasın."*

Sayılar `AosPropertyCoverageGuardrailTests` tarafından sabitlendi; kaynak dosya
yoksa test temiz atlar.

## Önce uyarının kendisi: bizde tersi geçerli

Plan, "tooltip'te görünüyor → etkisi tamam" yanılgısına karşı uyarıyor. Bu motorda
**bu yanılgı mümkün değil**, çünkü tooltip hiçbir component property'si
yayınlamıyor. Tooltip'in tamamı şu: ad, silah `DAM`/`SPEED`, zırh `ARMOR`/
`DURABILITY`, kap içerik sayısı, comm crystal satırları ve script'in eklediği
satırlar (`@ClientTooltip`). `RESFIRE`, `HITLEECHLIFE`, `FASTERCASTING` gibi
hiçbir AOS satırı yok.

Yani bizdeki risk ters yönde: **motorun uyguladığı bir etkiyi oyuncu göremiyor.**

## Asıl mesele: era kapısı

Referans 139 component property'sini, onları getiren genişlemeye göre etiketliyor
(`src/tables/CCProps*_props.tbl`, `RDS_*`):

| Era | Property sayısı |
|---|---:|
| AOS | 66 |
| PRET2A | 38 |
| SA | 26 |
| TOL | 5 |
| ML | 2 |
| HS | 2 |

> Bir ad birden çok component sınıfında farklı etiketle geçebilir — `NIGHTSIGHT`
> birinde PRET2A, ötekinde AOS. Klasik içeriğin onu kullanıp kullanamayacağını
> **en erken** etiket belirler, matris onu tutar.

## Kullanılan içerik

| Paket | 139'un kaçını atıyor |
|---|---:|
| **Canlı shard paketi** (`C:\sphereNetServer\scripts`) | **2** |
| `oldSphere/Scripts-X-main` (modern referans paket) | **59** |

Canlı paketin attığı iki property: **`NIGHTSIGHT`** ve **`RANGE`** — ikisi de
**PRET2A**, yani AOS property sisteminin parçası bile değil. Canlı shard 7.0.20
klasik istemciyle klasik-era bir paket çalıştırıyor; AOS+ item property sistemini
**hiç kullanmıyor**.

Karşılaştırma için modern pakette: `RESFIRE` 716 yerde, `HITLEECHLIFE` 13,
`FASTERCASTING` 8.

## Motor tarafı ve bu ölçümün sınırı

Motor (Core + Game + Scripting) 139 adın **67**'sini bir yerde anıyor. Modern
paketin attığı 59'un **10'u** motorda hiç geçmiyor:

`BALANCED`, `ENHANCEPOTIONS`, `HITLOWERATK`, `HITLOWERDEF`, `INCREASESPELLDAM`,
`LOWERREQ`, `MAGEARMOR`, `MAGEWEAPON`, `SPELLCHANNELING`, `USEBESTWEAPONSKILL`

**Bu sütunun sınırını açıkça yazmak gerekiyor:** "motor adı anıyor" ≠ "combat
etkisi doğrulandı". Bir ad yalnızca ayrıştırılıp bir alana yazılmış da olabilir.
Ters yönde de yanılır: `LOWERMANACOST` ve `FASTERCASTING` gibi property'ler
metin sabiti yerine `SpellCastingProperties` sabitleri üzerinden tüketiliyor,
yani ad araması onları kaçırabilir. Bu yüzden guardrail testi yalnızca
**savunulabilir** iki sütunu sabitliyor: referans era tablosu ve iki paketin
kullanımı. Etki sütunu bu belgede bilgi amaçlı, teste bağlanmadı.

## Sonuç — iş sırası için

AOS+ property işi, **çalışan shard'da tüketicisi olmayan** bir iştir. Yukarıdaki
10 eksik property yalnızca modern paket çalıştırılırsa anlam kazanır. İŞ-8'in
paket-tarafı ölçüm kuralı gereği bu dalgada hiçbiri yazılmadı; bu belge
sıralamanın gerekçesidir.

Shard bir gün modern pakete geçerse başlangıç noktası bu 10 property ve
tooltip'in AOS satırlarını hiç yayınlamaması olmalıdır.
