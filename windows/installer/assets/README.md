# Оформление установщика

- `WizardBanner.png` — вертикальный баннер для Windows, создан из корневого [Banner.png](../../../Assets/Banner.png) встроенным imagegen. Исходный баннер macOS не менялся.
- `Plantain.ico` — исходная иконка [AppIcon.png](../../../Assets/AppIcon.png) в формате ICO с размерами 16, 24, 32, 48, 64, 128 и 256 пикселей. Используется EXE, установщиком и ярлыками.
- `WizardIcon.png` — та же исходная иконка, 128 пикселей, для заголовка мастера.

## Промпт вертикального баннера

Использован встроенный инструмент imagegen, исходный `Assets/Banner.png` передан как изображение для редактирования:

> Use case: compositing / ads-marketing. Edit the attached existing Plantain / Подорожник banner into a narrow vertical installer sidebar with exact aspect ratio 164:314 (portrait, e.g. 656x1256). Preserve the existing brand: cream textured paper background, realistic gray gravestone with red R in RIP and Cyrillic ДИСК, green plantain leaves and grass. Recompose vertically without rotating or stretching the artwork. Top: brand name Подорожник in bold black Cyrillic, legible and fitting entirely within narrow width. Under it smaller exact slogan on 3 lines: скачает, / когда Диск / не может. Center and bottom: large gravestone with plantain leaves from original, ground at bottom, breathing room. Small text below slogan: для Windows. Remove all macOS text. No mockup, window frame, installation controls, borders or watermarks. Output only final flat vertical banner. Keep the original recognizable emblem and natural illustration style.
