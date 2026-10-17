# Ekranix протокол v1

| Порт | Тип | Предназначение |
|---|---|---|
| 47000 | UDP | Откриване |
| 47001 | TCP | Управление (JSON, по един ред с \n) |
| 47002 | TCP | Видео: [4 байта дължина big-endian][H.264 NAL блок] |
| 47003 | TCP | Звук: PCM s16le, 48000 Hz, stereo |

## Откриване

PC праща broadcast `EKRANIX_DISCOVER_V1` на UDP 47000.
TV отговаря директно на подателя:
{"type":"here","id":"<uuid>","name":"<име на TV>","controlPort":47001}