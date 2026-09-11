#!/usr/bin/env node
/**
 * Exporta históricos de Firestore al JSON que importa RegNeps.Net
 * (HistoricalDataMigrationService / tools/RegNeps.Migrate).
 *
 * Uso:
 *   node scripts/export_firestore_history.js --credentials secrets/serviceAccountKey.json --out FTS/firestore_export.json
 *   $env:GOOGLE_APPLICATION_CREDENTIALS="..." ; node scripts/export_firestore_history.js --out FTS/firestore_export.json
 *   firebase login ; node scripts/export_firestore_history.js --out FTS/firestore_export.json
 *
 * Colecciones / docs (workspace por defecto: vicunha):
 *   workspaces/{ws}/records
 *   workspaces/{ws}/users
 *   workspaces/{ws}/reports
 *   workspaces/{ws}/meta/fabrics
 *   workspaces/{ws}/meta/config
 */

"use strict";

const fs = require("fs");
const path = require("path");

function printHelp() {
  console.log(`Uso:
  node scripts/export_firestore_history.js [opciones]

Opciones:
  --credentials <ruta>   Service account JSON (recomendado)
  --out <ruta>           Archivo de salida (default: FTS/firestore_export.json)
  --workspace <id>       Workspace Firestore (default: vicunha)
  --project <id>         Project ID de Firebase (opcional si viene en el JSON)
  --help                 Muestra esta ayuda

Auth (en orden):
  1) --credentials
  2) GOOGLE_APPLICATION_CREDENTIALS
  3) Application Default Credentials (firebase login / gcloud auth application-default login)
`);
}

function parseArgs(argv) {
  const opts = {
    credentials: null,
    out: "FTS/firestore_export.json",
    workspace: "vicunha",
    project: null,
    help: false,
  };

  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--help" || a === "-h") {
      opts.help = true;
    } else if (a === "--credentials" || a === "--cred") {
      opts.credentials = argv[++i];
    } else if (a === "--out" || a === "-o") {
      opts.out = argv[++i];
    } else if (a === "--workspace" || a === "--ws") {
      opts.workspace = argv[++i];
    } else if (a === "--project") {
      opts.project = argv[++i];
    } else {
      throw new Error(`Argumento desconocido: ${a}`);
    }
  }

  if (!opts.out) {
    throw new Error("Falta valor para --out");
  }
  if (!opts.workspace) {
    throw new Error("Falta valor para --workspace");
  }
  return opts;
}

function resolveFromRepoRoot(...parts) {
  return path.resolve(process.cwd(), ...parts);
}

function loadAdmin() {
  try {
    return require("firebase-admin");
  } catch {
    const local = path.join(__dirname, "node_modules", "firebase-admin");
    try {
      return require(local);
    } catch {
      console.error(
        "No se encontró firebase-admin.\n" +
          "Ejecute: cd scripts && npm install"
      );
      process.exit(1);
    }
  }
}

function initFirebase(admin, opts) {
  if (admin.apps.length) {
    return admin.app();
  }

  const init = { credential: null, projectId: opts.project || undefined };

  if (opts.credentials) {
    const credPath = resolveFromRepoRoot(opts.credentials);
    if (!fs.existsSync(credPath)) {
      throw new Error(`No existe el archivo de credenciales: ${credPath}`);
    }
    const sa = JSON.parse(fs.readFileSync(credPath, "utf8"));
    init.credential = admin.credential.cert(sa);
    if (!init.projectId && sa.project_id) {
      init.projectId = sa.project_id;
    }
    console.log(`Auth: service account (${credPath})`);
  } else if (process.env.GOOGLE_APPLICATION_CREDENTIALS) {
    const envPath = process.env.GOOGLE_APPLICATION_CREDENTIALS;
    if (!fs.existsSync(envPath)) {
      throw new Error(
        `GOOGLE_APPLICATION_CREDENTIALS apunta a un archivo inexistente: ${envPath}`
      );
    }
    init.credential = admin.credential.applicationDefault();
    console.log(`Auth: GOOGLE_APPLICATION_CREDENTIALS (${envPath})`);
  } else {
    init.credential = admin.credential.applicationDefault();
    console.log(
      "Auth: Application Default Credentials (firebase login / gcloud ADC)"
    );
  }

  return admin.initializeApp(init);
}

/** Convierte Timestamps / Dates / referencias a JSON plano consumible por el importador C#. */
function serializeValue(value) {
  if (value == null) {
    return value;
  }

  if (typeof value === "object") {
    // Firestore Timestamp
    if (typeof value.toDate === "function" && typeof value.seconds === "number") {
      return {
        _seconds: value.seconds,
        _nanoseconds: value.nanoseconds || 0,
      };
    }
    if (value instanceof Date) {
      return value.toISOString();
    }
    // DocumentReference
    if (typeof value.path === "string" && typeof value.id === "string") {
      return value.path;
    }
    if (Array.isArray(value)) {
      return value.map(serializeValue);
    }
    const out = {};
    for (const [k, v] of Object.entries(value)) {
      out[k] = serializeValue(v);
    }
    return out;
  }

  return value;
}

function docToPlain(doc) {
  const data = doc.data() || {};
  return { id: doc.id, ...serializeValue(data) };
}

async function fetchAllDocs(collectionRef, FieldPath, pageSize = 500) {
  const rows = [];
  let last = null;
  for (;;) {
    let q = collectionRef.orderBy(FieldPath.documentId()).limit(pageSize);
    if (last) {
      q = q.startAfter(last);
    }
    const snap = await q.get();
    if (snap.empty) {
      break;
    }
    for (const doc of snap.docs) {
      rows.push(docToPlain(doc));
    }
    last = snap.docs[snap.docs.length - 1];
    if (snap.size < pageSize) {
      break;
    }
  }
  return rows;
}

function extractFabricNames(fabricsDocOrNull, fabricCollectionDocs) {
  const names = new Set();

  if (fabricsDocOrNull) {
    const data = fabricsDocOrNull;
    const candidates = [
      data.items,
      data.fabrics,
      data.nombres,
      data.names,
      data.list,
      data.values,
    ];
    for (const c of candidates) {
      if (Array.isArray(c)) {
        for (const item of c) {
          if (typeof item === "string" && item.trim()) {
            names.add(item.trim());
          } else if (item && typeof item === "object") {
            const n = item.name || item.nombre || item.tela;
            if (typeof n === "string" && n.trim()) {
              names.add(n.trim());
            }
          }
        }
      }
    }
    // Documento con claves = nombres
    if (names.size === 0) {
      for (const [k, v] of Object.entries(data)) {
        if (k === "id" || k.startsWith("_")) {
          continue;
        }
        if (typeof v === "string" && v.trim()) {
          names.add(v.trim());
        } else if (v === true || (v && typeof v === "object" && v.active !== false)) {
          names.add(k);
        }
      }
    }
  }

  for (const doc of fabricCollectionDocs || []) {
    const n = doc.name || doc.nombre || doc.tela || doc.id;
    if (typeof n === "string" && n.trim()) {
      names.add(n.trim());
    }
  }

  return [...names].sort((a, b) => a.localeCompare(b, "es"));
}

function extractAlertConfig(configDoc) {
  if (!configDoc) {
    return null;
  }
  const src = configDoc.alertConfig || configDoc.alertas || configDoc.config || configDoc;
  const keys = [
    "limiteNormalMax",
    "limiteAdvertenciaMax",
    "cantidadReincidenciasCriticas",
    "diasParaReincidencia",
    "alertasActivas",
  ];
  const out = {};
  let any = false;
  for (const k of keys) {
    if (src[k] !== undefined) {
      out[k] = src[k];
      any = true;
    }
  }
  return any ? out : serializeValue(src);
}

async function main() {
  const opts = parseArgs(process.argv.slice(2));
  if (opts.help) {
    printHelp();
    return;
  }

  const admin = loadAdmin();
  const app = initFirebase(admin, opts);
  const db = admin.firestore();
  const FieldPath = admin.firestore.FieldPath;
  const ws = opts.workspace;
  const base = db.collection("workspaces").doc(ws);

  console.log(`Workspace: ${ws}`);
  console.log("Leyendo records…");
  const records = await fetchAllDocs(base.collection("records"), FieldPath);
  console.log(`  ${records.length} registros`);

  console.log("Leyendo users…");
  const users = await fetchAllDocs(base.collection("users"), FieldPath);
  console.log(`  ${users.length} usuarios`);

  console.log("Leyendo reports…");
  const reports = await fetchAllDocs(base.collection("reports"), FieldPath);
  console.log(`  ${reports.length} informes`);

  console.log("Leyendo meta/fabrics…");
  let fabricsDoc = null;
  const fabricsSnap = await base.collection("meta").doc("fabrics").get();
  if (fabricsSnap.exists) {
    fabricsDoc = { id: fabricsSnap.id, ...serializeValue(fabricsSnap.data() || {}) };
  }
  // Por si fabrics es subcolección en vez de documento único
  let fabricCollectionDocs = [];
  try {
    fabricCollectionDocs = await fetchAllDocs(
      base.collection("meta").doc("fabrics").collection("items"),
      FieldPath
    );
  } catch {
    fabricCollectionDocs = [];
  }
  if (fabricCollectionDocs.length === 0) {
    try {
      fabricCollectionDocs = await fetchAllDocs(base.collection("fabrics"), FieldPath);
    } catch {
      fabricCollectionDocs = [];
    }
  }
  const fabrics = extractFabricNames(fabricsDoc, fabricCollectionDocs);
  console.log(`  ${fabrics.length} telas`);

  console.log("Leyendo meta/config…");
  let alertConfig = null;
  const configSnap = await base.collection("meta").doc("config").get();
  if (configSnap.exists) {
    alertConfig = extractAlertConfig({
      id: configSnap.id,
      ...serializeValue(configSnap.data() || {}),
    });
  }
  console.log(`  config: ${alertConfig ? "ok" : "ausente"}`);

  const payload = {
    exportedAt: new Date().toISOString(),
    projectId: opts.project || app.options.projectId || null,
    workspaceId: ws,
    records,
    users,
    reports,
    fabrics,
    alertConfig,
  };

  const outPath = resolveFromRepoRoot(opts.out);
  fs.mkdirSync(path.dirname(outPath), { recursive: true });
  fs.writeFileSync(outPath, JSON.stringify(payload, null, 2), "utf8");

  console.log("");
  console.log(`Export listo: ${outPath}`);
  console.log(
    `Resumen → records=${records.length}, users=${users.length}, reports=${reports.length}, fabrics=${fabrics.length}`
  );
  console.log("Siguiente: importar en /migracion o con tools/RegNeps.Migrate");
}

main().catch((err) => {
  const msg = err && err.message ? err.message : String(err);
  console.error("\nError al exportar:", msg);
  if (/UNAUTHENTICATED|Unauthorized|invalid_grant|Could not load the default credentials/i.test(msg)) {
    console.error(`
Autenticación fallida. Pruebe:
  1) Service account:
     node scripts/export_firestore_history.js --credentials secrets/serviceAccountKey.json --out FTS/firestore_export.json
  2) Variable de entorno:
     $env:GOOGLE_APPLICATION_CREDENTIALS="C:\\ruta\\serviceAccountKey.json"
  3) ADC:
     gcloud auth application-default login
     (o firebase login + credenciales de aplicación)
`);
  }
  process.exit(1);
});
