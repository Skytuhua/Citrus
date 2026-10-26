#!/usr/bin/env python3
"""Restore only integrity-pinned offline engine inputs from the first public pack.

The frozen installer is read as an archive; it is never executed. Existing source
or engine bytes are never replaced. A changed pin requires a new reviewed pack.
Existing matching files retain their ACLs. Repair an older protected hydration
in a fresh workspace; this tool never resets existing developer file security.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile
import urllib.request

ROOT=Path(__file__).resolve().parents[1]
URL='https://github.com/Skytuhua/Citrus/releases/download/v0.3.0-preview.1/Citrus-Setup.exe'
BYTES=1302313670
SHA256='a6d3dfcccf2b6627f3499507795a7ef7c4eacda4274034abc57c7f583c0cdae8'

def digest(path):
    result=hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda:stream.read(1024*1024),b''):result.update(block)
    return result.hexdigest()

def checked_destination(path):
    path=Path(path)
    if not path.resolve().is_relative_to(ROOT.resolve()):raise RuntimeError('Engine destination escaped workspace')
    relative=path.relative_to(ROOT)
    current=ROOT
    for part in relative.parts:
        current=current/part
        try:info=current.lstat()
        except FileNotFoundError:break
        if stat.S_ISLNK(info.st_mode) or getattr(info,'st_file_attributes',0)&0x400:
            raise RuntimeError('Linked engine ancestor refused: '+str(current))
    return path

def locked_files():
    result=[]
    media=json.loads((ROOT/'packaging/media/sha256.json').read_text(encoding='utf-8-sig'))
    for name,sha in media.items():
        published=name.replace('win-x64/','media/',1)
        result.append((ROOT/'packaging/media'/name,published,sha,None))
    image=json.loads((ROOT/'packaging/image-backend.lock.json').read_text(encoding='utf-8-sig'))['offlineFeaturePack']['files']
    for item in image:
        name=item['path']
        published=('image/'+name[len('win-x64/'):] if name.startswith('win-x64/') else 'source/image/'+name[len('source/'):] if name.startswith('source/') else 'source/image/'+name)
        result.append((ROOT/'packaging/image'/name,published,item['sha256'],item.get('bytes')))
    catalog=json.loads((ROOT/'packaging/feature-pack-runtime.sha256.json').read_text(encoding='utf-8-sig'))['files']
    for item in catalog:
        result.append((ROOT/'packaging/feature-pack'/item['path'],'feature-pack/'+item['path'],item['sha256'],item.get('bytes')))
    lock=json.loads((ROOT/'packaging/feature-pack.lock.json').read_text(encoding='utf-8-sig'))
    for item in lock['sources']:
        result.append((ROOT/'packaging/feature-pack'/item['file'],'source/feature-pack/'+item['file'][len('source/'):],item['sha256'],item.get('bytes')))
    for item in lock['trustedWorkers']:
        result.append((ROOT/'packaging/feature-pack'/item['file'],'feature-pack/'+item['file'],item['sha256'],None))
    return result

def valid(path,sha,size):
    if path.is_symlink():raise RuntimeError('Linked engine path refused: '+str(path))
    return path.is_file() and (size is None or path.stat().st_size==size) and digest(path)==sha

def materialize(source,destination):
    # A fresh file inherits the bundle's ACL instead of retaining protection
    # from the private extraction directory. Never copy source security metadata.
    created=False
    try:
        with source.open('rb') as incoming,destination.open('xb') as outgoing:
            created=True
            shutil.copyfileobj(incoming,outgoing,1024*1024)
    except BaseException:
        if created:destination.unlink(missing_ok=True)
        raise
    source.unlink()

class HttpsRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self,request,fp,code,msg,headers,newurl):
        if not newurl.startswith('https://'):raise RuntimeError('Refusing non-HTTPS release redirect')
        return super().redirect_request(request,fp,code,msg,headers,newurl)

def restore(installer=None):
    records=locked_files();missing=[]
    for destination,published,sha,size in records:
        checked_destination(destination)
        if destination.exists():
            if not valid(destination,sha,size):raise RuntimeError('Existing engine differs from repository pin: '+str(destination))
        else:missing.append((destination,published,sha,size))
    if not missing:
        print('PASS all '+str(len(records))+' offline inputs already match repository pins; no download')
        return
    workroot=checked_destination(ROOT/'.local/engine-bootstrap');workroot.mkdir(parents=True,exist_ok=True)
    owned_download=installer is None
    installer=Path(installer).resolve() if installer else workroot/'Citrus-0.3.0-preview.1.exe'
    if not installer.exists():
        part=installer.with_suffix('.part')
        opener=urllib.request.build_opener(HttpsRedirects())
        request=urllib.request.Request(URL,headers={'User-Agent':'Citrus-Pinned-Engine-Bootstrap'})
        received=0
        with opener.open(request,timeout=90) as response,part.open('wb') as output:
            if response.status!=200:raise RuntimeError('Frozen engine pack download failed')
            for block in iter(lambda:response.read(1024*1024),b''):
                received+=len(block)
                if received>BYTES:raise RuntimeError('Frozen pack exceeds pinned size')
                output.write(block)
        if not valid(part,SHA256,BYTES):raise RuntimeError('Frozen engine pack SHA-256/size mismatch')
        part.replace(installer)
    if not valid(installer,SHA256,BYTES):raise RuntimeError('Frozen installer must match the reviewed public pack')
    seven=os.environ.get('SEVEN_ZIP') or shutil.which('7z') or r'C:\Program Files\7-Zip\7z.exe'
    with tempfile.TemporaryDirectory(prefix='extract-',dir=workroot) as temporary:
        extraction=Path(temporary)
        subprocess.run([seven,'x','-y','-bd','-bso0','-o'+str(extraction),str(installer),'media/*','image/*','feature-pack/*','source/*'],check=True)
        for destination,published,sha,size in missing:
            source=extraction/Path(published)
            if not valid(source,sha,size):raise RuntimeError('Frozen pack does not supply current pin: '+published)
            checked_destination(destination)
            destination.parent.mkdir(parents=True,exist_ok=True)
            if destination.exists():raise RuntimeError('Engine appeared during hydration; refusing overwrite')
            # Hashes are checked again below after exclusive data-only copying.
            materialize(source,destination)
    for destination,published,sha,size in records:
        if not valid(destination,sha,size):raise RuntimeError('Hydrated engine verification failed: '+published)
    if owned_download:installer.unlink()
    print('PASS restored '+str(len(missing))+' offline inputs; all '+str(len(records))+' repository hashes match')

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--installer',type=Path)
    restore(parser.parse_args().installer)
