# ML Kit (the QR scanner) creates the component registrars named in the merged manifest by reflection through
# their no-argument constructors. R8 full mode removed those constructors and the scanner crashed on open.
-keep class * implements com.google.firebase.components.ComponentRegistrar { <init>(); }
