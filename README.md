<div align="center">
  <img src="CS2Blocker/Assets/cs2.png" width="64" height="64" alt="Counter-Strike 2">
  <h1>CS2 Blocker</h1>
  <p>Блокировка реле Counter-Strike 2 по странам для Windows.</p>
  <p>
    <img src="https://img.shields.io/badge/Windows-x64-151719?style=flat-square&logo=windows&logoColor=EDB54D" alt="Windows x64">
    <img src="https://img.shields.io/badge/.NET-8-151719?style=flat-square&logo=dotnet&logoColor=EDB54D" alt=".NET 8">
    <img src="https://img.shields.io/badge/Avalonia-12.1-151719?style=flat-square" alt="Avalonia 12.1">
  </p>
  <p><a href="https://github.com/akzor/CS2Blocker/releases/latest"><strong>Скачать для Windows</strong></a> · <a href="#быстрый-старт">Быстрый старт</a> · <a href="#сборка-из-исходников">Сборка</a></p>
</div>

---

Небольшая Windows-утилита для блокировки реле **Counter-Strike 2** по странам через Windows Firewall. Актуальные локации и IP загружаются из каталога Valve. Готовый `.exe` работает без отдельной установки .NET.

## Возможности

| | Что делает |
| :-- | :-- |
| **Страны** | Автоблокировка выбранных стран, включая новые IP после обновления каталога. |
| **Массовый выбор** | «Все страны» применяет весь список одним действием. «Снять выбор» удаляет только автоматические правила. |
| **Отдельные локации** | Ручная блокировка и разблокировка выбранных точек. |
| **Обновление каталога** | Каждые 12 часов при открытом приложении; пропущенное обновление выполняется после запуска. |
| **Пинг** | Проверка задержки с возможностью остановки. |
| **Трей и автозапуск** | Работа в фоне и запуск вместе с Windows по выбору пользователя. |
| **Права администратора** | Перезапуск с повышенными правами из настроек. При отмене UAC текущая копия остаётся открытой. |

## Быстрый старт

1. Скачай `CS2Blocker.exe` из [последнего релиза](https://github.com/akzor/CS2Blocker/releases/latest).
2. Запусти приложение и открой настройки шестерёнкой.
3. Нажми **«Перезапустить от администратора»** и подтверди запрос Windows.
4. Отметь страны в разделе **«Автоблокировка»** или нажми **«Все страны»**. Правила применяются сразу.

Для работы с отдельными локациями отметь строки в таблице и используй **«Блокировать»** / **«Разблокировать»**.

### Как снять блокировку

- **«Снять выбор»** в списке стран — отключить автоблокировку, сохранив ручные правила.
- **«Снять всё»** — удалить правила `CS2_Block_*` и отключить автоблокировку стран.

Закрытие приложения не удаляет уже созданные правила Windows Firewall.

## Обновления и настройки

Проверка каталога работает, пока приложение запущено, в том числе в трее. Автозапуск включается в настройках и запускает приложение в трее; для изменения блокировок нужны права администратора.

Выбор стран и кеш каталога сохраняются в:

```text
%LOCALAPPDATA%\CS2Blocker\settings.json
```

Локации без опубликованных IP отображаются в списке. Если страна выбрана, появившиеся адреса попадут под автоблокировку при следующем успешном обновлении.

## Сборка из исходников

**Требования:** Windows x64, .NET SDK 8 или новее. Для анализаторов Avalonia рекомендуется современный SDK; целевая платформа приложения — .NET 8.

```powershell
git clone https://github.com/akzor/CS2Blocker.git
cd CS2Blocker
dotnet build CS2Blocker/CS2Blocker.csproj -c Release
dotnet run --project CS2Blocker/CS2Blocker.csproj
dotnet publish CS2Blocker/CS2Blocker.csproj -c Release -o release
```

Готовый файл после публикации: `release/CS2Blocker.exe`.

## Ресурсы

- [Avalonia](https://github.com/AvaloniaUI/Avalonia) — интерфейс.
- [Tabler Icons](https://github.com/tabler/tabler-icons) — иконки, [MIT](CS2Blocker/Assets/icons/LICENSE.txt).
- [IBM Plex Sans](https://github.com/IBM/plex) и [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) — шрифты, лицензии находятся в [Assets/fonts](CS2Blocker/Assets/fonts).
- [Flagpedia / Flagcdn](https://flagpedia.net/) — изображения флагов.
- Изображение Counter-Strike 2 — Valve, [Steam](https://steamcommunity.com/app/730).

Неофициальная утилита, не связанная с Valve. Counter-Strike 2 и Steam принадлежат их правообладателям.

