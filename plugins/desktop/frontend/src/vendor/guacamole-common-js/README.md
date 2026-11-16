# Apache Guacamole JavaScript client (vendored)

`guacamole.js` is `guacamole-common-js/all.js` from the official **Apache Guacamole 1.6.0**
release, licensed under the Apache License 2.0. It matches the `guacamole/guacd:1.6.0` image.

It is vendored rather than installed from npm because the `guacamole-common-js` npm package
is a third-party repackaging, not published by Apache.

Provenance:

- Source: `https://repo1.maven.org/maven2/org/apache/guacamole/guacamole-common-js/1.6.0/guacamole-common-js-1.6.0.zip`
- Zip SHA-1 (matches Maven Central's `.sha1`): `bf45c3c0773999232cd65f624749cefcc8db2b9c`
- Zip signature: verified against `https://downloads.apache.org/guacamole/KEYS`
  ("Michael Jumper (CODE SIGNING KEY) <mjumper@apache.org>", F467 E54A CC52 F1D2 7788 2686 5B29 77AE E5E4 518F)
- `all.js` SHA-256: `b31a4384599f7498bcabf91ed934124ea5df30231310aa78407d3c3754287633`

The only change is the final `export default Guacamole;` line (plus its comment), which makes
the file an ES module. To verify: remove those last lines and compare the SHA-256 above.
To upgrade, repeat the steps with the new version and update guacd to the same version.
