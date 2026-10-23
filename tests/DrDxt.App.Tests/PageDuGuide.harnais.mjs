// Exécute le script de docs/guide-bios/index.html, tel quel, sur un DOM minimal, et écrit en JSON ce que chaque élément
// affiche. Les tests de GuideBiosParLaPageTests jugent ainsi la page elle-même, pas une copie de ses règles.
// Usage : node PageDuGuide.harnais.mjs <index.html> <fragment> <langue du téléphone>
import { readFileSync } from "node:fs";

const [chemin, fragment, langue] = process.argv.slice(2);
const html = readFileSync(chemin, "utf8");
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];

class Element {
  constructor(id = "", hidden = false) { this.id = id; this.hidden = hidden; this.texte = ""; this.enfants = []; this.className = ""; }
  set textContent(t) { this.texte = String(t); this.enfants = []; }
  get textContent() { return this.texte + this.enfants.map((e) => (typeof e === "string" ? e : e.textContent)).join(""); }
  append(...x) { this.enfants.push(...x); }
}

const elements = {};
for (const m of html.matchAll(/<(\w+)([^>]*)\bid="([^"]+)"([^>]*)>/g)) {
  const e = new Element(m[3], /\shidden(\s|>|$)/.test(m[2] + m[4] + ">"));
  if (m[1] === "script") e.texte = html.slice(m.index + m[0].length, html.indexOf("</script>", m.index));
  elements[m[3]] = e;
}

globalThis.document = {
  getElementById: (id) => elements[id] ?? null,
  createElement: () => new Element(),
  createTextNode: (t) => String(t),
  documentElement: { lang: "" },
};
globalThis.location = { hash: "#" + fragment };
Object.defineProperty(globalThis, "navigator", { value: { language: langue }, configurable: true });

new Function(script)();
await new Promise((r) => setTimeout(r, 300));

// Visible : l'élément n'est pas caché, ni le bloc du guide quand il en fait partie.
const horsDuGuide = new Set(["titre", "erreur", "pied", "guide"]);
const sortie = {};
for (const [id, e] of Object.entries(elements)) {
  if (html.includes(`id="${id}">`) && /type="application\/json"/.test(html.slice(html.lastIndexOf("<", html.indexOf(`id="${id}"`)), html.indexOf(`id="${id}"`)))) continue;
  sortie[id] = { visible: !e.hidden && (horsDuGuide.has(id) || !elements.guide.hidden), texte: e.textContent };
}
process.stdout.write(JSON.stringify(sortie));
