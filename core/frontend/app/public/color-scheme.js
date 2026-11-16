// Applies the last color scheme before the first paint, so a reload doesn't flash the default colors.
// A plain file, not inline: the Content-Security-Policy allows only same-origin scripts.
// The tokens are written by applyColorScheme in src/appearance/colorSchemes.ts.
try {
  var tokens = JSON.parse(localStorage.getItem('nkeeper.colorSchemeTokens') || '{}')
  Object.keys(tokens).forEach(function (name) {
    document.documentElement.style.setProperty(name, tokens[name])
  })
} catch {
  // Nothing saved or storage blocked: the default colors apply.
}
