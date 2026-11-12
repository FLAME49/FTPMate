import ftplib
import io
import sys

HOST = "127.0.0.1"
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 2121
USER = sys.argv[2] if len(sys.argv) > 2 else "anonymous"
PASS = sys.argv[3] if len(sys.argv) > 3 else ""

failures = []

def check(name, fn):
    try:
        fn()
        print("PASS", name)
    except Exception as exc:
        failures.append(name)
        print("FAIL", name, "->", type(exc).__name__, exc)

ftp = ftplib.FTP()
ftp.encoding = "utf-8"
ftp.connect(HOST, PORT, timeout=10)

def login():
    welcome = ftp.login(USER, PASS)
    print("welcome:", welcome.splitlines()[0])

def listing():
    lines = []
    ftp.retrlines("LIST", lines.append)
    assert any("seed.txt" in line for line in lines), "seed.txt missing in LIST"
    assert any("subdir" in line for line in lines), "subdir missing in LIST"
    print("LIST sample:", lines[-1])

def nlst():
    names = ftp.nlst()
    assert "seed.txt" in names, names
    print("NLST:", names)

def roundtrip():
    data = b"hello ftp share " * 5000
    ftp.storbinary("STOR test.bin", io.BytesIO(data))
    got = bytearray()
    ftp.retrbinary("RETR test.bin", got.extend)
    assert bytes(got) == data, "content mismatch"
    print("transfer bytes:", len(got))

def navigation():
    ftp.cwd("subdir")
    assert ftp.pwd() == "/subdir", ftp.pwd()
    ftp.cwd("/")
    assert ftp.pwd() == "/", ftp.pwd()

def folders():
    ftp.mkd("Café Folder")
    ftp.cwd("Café Folder")
    ftp.storbinary("STOR résumé.txt", io.BytesIO("Olá mundo\n".encode("utf-8")))
    names = ftp.nlst()
    assert any("résumé.txt" in n for n in names), names
    ftp.rename("résumé.txt", "résumé2.txt")
    ftp.delete("résumé2.txt")
    ftp.cwd("/")
    ftp.rmd("Café Folder")

def size_and_mdtm():
    assert ftp.size("test.bin") == 80000, ftp.size("test.bin")
    print("MDTM:", ftp.sendcmd("MDTM test.bin"))

def traversal():
    for bad in ["../../../Windows/win.ini", "..\\..\\..\\Windows\\win.ini", "/../etc/passwd"]:
        try:
            ftp.size(bad)
        except ftplib.error_perm:
            continue
        raise AssertionError("traversal allowed: " + bad)
    print("traversal attempts blocked")

def cleanup():
    ftp.delete("test.bin")

check("login", login)
check("LIST", listing)
check("NLST", nlst)
check("upload+download", roundtrip)
check("navigation", navigation)
check("unicode folders", folders)
check("size+mdtm", size_and_mdtm)
check("path traversal", traversal)
check("cleanup", cleanup)

try:
    ftp.quit()
except Exception:
    pass

print("RESULT", "OK" if not failures else "FAILED " + ",".join(failures))
sys.exit(0 if not failures else 1)
