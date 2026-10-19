import socket, struct, sys

host, port, password, commands = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4:]


def pack(request_id, kind, body):
    payload = struct.pack("<ii", request_id, kind) + body.encode() + b"\x00\x00"
    return struct.pack("<i", len(payload)) + payload


def unpack(sock):
    header = b""
    while len(header) < 4:
        header += sock.recv(4 - len(header))
    size = struct.unpack("<i", header)[0]
    data = b""
    while len(data) < size:
        data += sock.recv(size - len(data))
    request_id, _ = struct.unpack("<ii", data[:8])
    return request_id, data[8:-2].decode(errors="replace")


sock = socket.create_connection((host, port), timeout=10)
sock.send(pack(1, 3, password))
while True:
    request_id, _ = unpack(sock)
    if request_id == -1:
        sys.exit("rcon auth failed")
    if request_id == 1:
        break

for index, command in enumerate(commands, start=2):
    sock.send(pack(index, 2, command))
    print(f"--- {command}")
    print(unpack(sock)[1])
