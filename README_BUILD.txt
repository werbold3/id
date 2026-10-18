CARA BUILD DI GITHUB
1. Buat repo GitHub, upload semua isi folder ini (termasuk .github/workflows/build.yml).
2. Dari instalasi P3D SDK 4.5 salin ke folder lib/ :
   - LockheedMartin.Prepar3D.SimConnect.dll   (wajib)
   - SimConnect.dll (native, jika ada di folder SDK lib\SimConnect)
   Catatan: DLL ini milik Lockheed Martin. Pakai repo PRIVATE.
3. Commit & push. Tab "Actions" -> hasil build ada di Artifacts (AxisClone-win-x64).
4. Untuk release: git tag v1.0 && git push --tags
