# Contain package

Platform: linux / x86_64

- package_identity — 通过: actual ZIP SHA-256 3654437da063c1642adadfcab9572eac0550822161b08912671d23e4b7baf21d; expected 3654437da063c1642adadfcab9572eac0550822161b08912671d23e4b7baf21d
- static_entry — 通过: app.exe; EXE SHA-256 db619c3e023ecffd5aa56dfe0fc344b9cc7251d2f1eb7c3e4010bb2b9bda0e1e; Windows x64 PE header checked (not a loader test)
- static_file:critical.dll — 通过: required package member
- static_test:synthetic self-test NOT EXECUTABLE — 通过: test executable must be present in package
- comparison_identity — 失败: entry app.exe; baseline EXE SHA-256 db619c3e023ecffd5aa56dfe0fc344b9cc7251d2f1eb7c3e4010bb2b9bda0e1e; candidate EXE SHA-256 db619c3e023ecffd5aa56dfe0fc344b9cc7251d2f1eb7c3e4010bb2b9bda0e1e; identical bytes; different-build comparison rejected before execution
- launch — 未实测: static/identity gate failed; execution blocked
- synthetic self-test NOT EXECUTABLE — 未实测: static/identity gate failed; execution blocked
