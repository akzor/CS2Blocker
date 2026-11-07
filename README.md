<div align="center">
  <img src="CS2Blocker/Assets/cs2.png" width="64" height="64" alt="Counter-Strike 2">
  <h1>CS2 Blocker</h1>
  <p>Блокировка реле CS2 по странам через Windows Firewall.</p>
  <p>
    <img src="https://img.shields.io/badge/Windows-x64-151719?style=flat-square&logo=windows&logoColor=EDB54D" alt="Windows x64">
    <img src="https://img.shields.io/badge/.NET-8-151719?style=flat-square&logo=dotnet&logoColor=EDB54D" alt=".NET 8">
    <img src="https://img.shields.io/badge/Avalonia-12.1-151719?style=flat-square" alt="Avalonia 12.1">
  </p>
  <p><a href="https://github.com/akzor/CS2Blocker/releases/latest"><strong>Скачать для Windows</strong></a> · <a href="#быстрый-старт">Быстрый старт</a> · <a href="#сборка-из-исходников">Сборка</a></p>
</div>

![Интерфейс CS2 Blocker: выбор стран и список серверов](docs/screenshot.png)

---

CS2 Blocker загружает список реле из API Valve и блокирует их IP через Windows Firewall. Можно выбрать страны целиком или отдельные локации.

Готовый `.exe` работает без установки .NET.

## Возможности

- Выбор стран по одной или всех сразу с отдельной кнопкой применения.
- Автоблокировка новых IP выбранных стран.
- Обновление списка Valve каждые 12 часов.
- Блокировка и разблокировка отдельных локаций.
- Проверка пинга с кнопкой остановки.
- Сворачивание в трей и автозапуск с Windows.
- Компактное окно для небольших экранов и журнал операций.

## Быстрый старт

1. Скачай `CS2Blocker.exe` из [последнего релиза](https://github.com/akzor/CS2Blocker/releases/latest).
2. Открой настройки шестерёнкой и нажми **«Перезапустить от администратора»**.
3. Подтверди запрос Windows.
4. Отметь нужные страны в **«Автоблокировке»** или нажми **«Все страны»**, затем нажми **«Применить»**. Ход операции виден сверху, последние события доступны в **«Журнале»**.

Отдельные локации можно отметить в таблице и заблокировать кнопкой **«Блокировать»**. Для снятия ручных блокировок используй **«Разблокировать»**.

### Как снять блокировку

- **«Снять выбор»**, затем **«Применить»** — убрать автоматические правила. Ручные блокировки останутся.
- **«Снять всё»** — удалить все правила приложения `CS2_Block_*` и отключить автоблокировку.

Закрытие приложения не снимает блокировки.

## Обновления и автозапуск

Если установлена версия 0.1.0, скачай 0.2.0 вручную. Начиная с 0.2.0 приложение проверяет GitHub Releases при запуске и раз в 15 минут. Оно сверяет SHA-256 нового `CS2Blocker.exe`, заменяет файл и перезапускается, когда текущая операция завершена и выбор стран применён.

В настройках видна текущая версия. Кнопка **«Проверить»** запускает проверку сразу.

Список обновляется, пока приложение открыто, в том числе в трее. Если 12 часов прошли при закрытом приложении, проверка начнётся после следующего запуска.

Автозапуск включается в настройках. Он запускает приложение в трее, но не выдаёт права администратора: для изменения правил нужен перезапуск с повышенными правами.

Выбор стран и кеш списка сохраняются в `%LOCALAPPDATA%\CS2Blocker\settings.json`.

Локации без IP тоже видны в списке. Когда Valve опубликует адреса, они будут заблокированы при следующем обновлении, если страна выбрана.

## Сборка из исходников

Нужны Windows x64 и .NET SDK 8 или новее.

```powershell
git clone https://github.com/akzor/CS2Blocker.git
cd CS2Blocker
dotnet publish CS2Blocker/CS2Blocker.csproj -c Release -o release
```

Результат: `release/CS2Blocker.exe`.

## Ресурсы

- [Avalonia](https://github.com/AvaloniaUI/Avalonia) — интерфейс.
- [Tabler Icons](https://github.com/tabler/tabler-icons) — иконки, [MIT](CS2Blocker/Assets/icons/LICENSE.txt).
- [IBM Plex Sans](https://github.com/IBM/plex) и [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) — шрифты, лицензии находятся в [Assets/fonts](CS2Blocker/Assets/fonts).
- [Flagpedia / Flagcdn](https://flagpedia.net/) — изображения флагов.
- Изображение Counter-Strike 2 — Valve, [Steam](https://steamcommunity.com/app/730).

Неофициальная утилита, не связанная с Valve.
