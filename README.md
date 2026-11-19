# VSA & Orderflow v3.0 · 100 % en GitHub (Gratis e ilimitado)

Esta versión corre **solo con GitHub**:
- **GitHub Actions** vigila tus alertas de precio **cada 15 segundos** (en ciclos automáticos cada 5 minutos) y revisa señales confirmadas (`con respaldo`) en velas cerradas, avisándote por **Telegram** 24/7.
- **GitHub Pages** te aloja la web (`index.html`) para abrirla desde el celular en `https://TU_USUARIO.github.io/TU_REPO/`.

## Pasos (5 minutos)
1. **Creá un repositorio PÚBLICO en GitHub**
   - ¿Por qué público? Porque en repositorios públicos **GitHub Actions tiene minutos ilimitados gratis** (en repos privados el límite gratuito es 2.000 min/mes).
   - Es 100 % seguro porque tu Token y Chat ID **NO están en los archivos**, sino cifrados en **GitHub Secrets**.
2. **Subí todos los archivos de esta carpeta al repositorio** (incluida la carpeta `.github/workflows/vsa_monitor.yml`).
3. **Cargá tus Secrets** (en tu repo → **Settings → Secrets and variables → Actions → New repository secret**):
   - Nombre: `TELEGRAM_TOKEN` → Valor: tu Bot Token
   - Nombre: `TELEGRAM_CHAT_ID` → Valor: tu Chat ID
4. **Probalo ya mismo**:
   - Andá a la pestaña **Actions → VSA & Orderflow Monitor (Telegram) → Run workflow**.
   - Te va a llegar un mensaje de prueba a Telegram al instante.
5. **Opcional — Activar la web en el celular**:
   - En **Settings → Pages**, elegí **Deploy from a branch → main / (root)** y guardá.
6. **Para editar alertas de precio**:
   - Editá `config.json` directo desde GitHub (el ícono del lápiz ✏️) y agregá o cambiá los objetos en `"priceAlerts"`.
