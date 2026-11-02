# Keygen program for Siglent oscilloscopes
import hashlib

# 运行MD5_SRLN? 命令获得SCOPEID
# 将下面的SCOPEID替换为本机ID
SCOPEID = '01711b652d726605'

# 将下面的SN替换为本机代码
SN = 'SDS08A0CA00117'

# 运行MD5_PR?命令获得本机型号
# 将下面的Model 替换为本机型号"
Model = 'SDS800X-HD'

bwopt = ('70M', '100M','200M')
otheropt = ('AWG', 'MSO', 'PWA',)

# 不要修改这里 !!!
hashkey = '5zao9lyua01pp7hjzm3orcq90mds63z6zi5kv7vmv3ih981vlwn06txnjdtas3u2wa8msx61i12ueh14t7kqwsfskg032nhyuy1d9vv2wm925rd18kih9xhkyilobbgy'

def gen(x):
    h = hashlib.md5((
        hashkey +
        (Model+'\n').ljust(32, '\x00') +
        opt.ljust(5, '\x00') +
        2*(((SCOPEID if opt in bwopt else SN) + '\n').ljust(32, '\x00')) +
        '\x00'*16).encode('ascii')
    ).digest()
    key = ''
    for b in h:
        if (b <= 0x2F or b > 0x39) and (b <= 0x60 or b > 0x7A):
            m = b % 0x24
            b = m + (0x57 if m > 9 else 0x30)
        if b == 0x30:
            b = 0x32
        if b == 0x31:
            b = 0x33
        if b == 0x6c:
            b = 0x6d
        if b == 0x6f:
            b = 0x70
        key += chr(b)
    return key.upper()

print('INFO: 在输入新密钥之前检查生成的密钥 !!!' + '\r')
print('您可以通过在SCIP提示符下运行 “PRBD?” 来获得带宽' + '\r')
print('您可以通过在SCIP提示符下运行 “MCBD?” 来获得带宽密钥' + '\r')
print('将您的密钥与生成的密钥进行比较')
print('INFO: 本机带宽查询到的秘钥必须与脚本生成的带宽秘钥是相同的 !!!')

print('--------------------------------')
print('以下是带宽代码，通过SCIP提示输入这些代码')
for opt in bwopt:
    print('{:5} {}'.format(opt, gen(SCOPEID)))

print('--------------------------------')
print('以下是选件代码，在示波器UI上输入这些代码')
print('SDS800XHD-FG   选件输入AWG后代码')
print('SDS800XHD-16LA 选件输入MSO后代码')
print('SDS800XHD-PA   选件输入PWA后代码')
for opt in otheropt:
    print('{:5} {}'.format(opt, gen(SN)))
