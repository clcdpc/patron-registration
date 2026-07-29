(() => {
 const search=document.querySelector('#setting-search');
 search?.addEventListener('input',()=>{const q=search.value.toLowerCase();document.querySelectorAll('.setting-row').forEach(r=>{const match=r.dataset.search.includes(q);r.hidden=!match;if(match&&q)r.closest('details')?.setAttribute('open','');});});
 document.querySelectorAll('.html-preview').forEach(frame=>{const source=frame.previousElementSibling;const render=()=>frame.srcdoc=source.value;source.addEventListener('input',render);render();});
 const form=document.querySelector('#settings-form'), dialog=document.querySelector('#save-confirm'); let approved=false;
 form?.querySelectorAll('.setting-row').forEach(row=>row.querySelectorAll('input,select,textarea').forEach(x=>x.addEventListener('change',()=>row.dataset.dirty='true')));
 form?.addEventListener('submit',e=>{if(approved){form.querySelectorAll('.setting-row:not([data-dirty="true"]) [name]').forEach(x=>x.disabled=true);return;}e.preventDefault();const list=dialog.querySelector('ul');list.replaceChildren();form.querySelectorAll('.setting-row[data-dirty="true"]').forEach(row=>{const value=row.querySelector('.setting-value'),op=row.querySelector('.operation');const li=document.createElement('li');li.textContent=`${row.querySelector('code').textContent}: ${op.value} → ${value.type==='password'?'••••••••':value.value}`;list.append(li);});if(!list.children.length){window.alert('No settings have changed.');return;}dialog.showModal();});
 document.querySelector('#confirm-save')?.addEventListener('click',()=>{approved=true;form.requestSubmit();});document.querySelector('#cancel-save')?.addEventListener('click',()=>dialog.close());
})();
