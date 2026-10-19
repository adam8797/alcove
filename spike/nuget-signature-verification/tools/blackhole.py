# A TCP listener that accepts connections and never answers: simulates a network where requests hang.
import socket, sys, time, threading
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(("127.0.0.1", int(sys.argv[1]))); s.listen(100)
conns = []
while True:
    c, _ = s.accept(); conns.append(c)
