var scriptTag = document.querySelector('script[data-org-id]');
var baseUrl = new URL(scriptTag.src).origin;

var orgId = scriptTag.dataset.orgId;
var formCode = scriptTag.dataset.formCode;

var formUrl = baseUrl + `/Create/${orgId}`;
if (formCode) { formUrl += `/${formCode}`; }

var kiosk = scriptTag.dataset.kiosk;
if (kiosk === 'true') { formUrl += '?kiosk=true'; }

var formHostId = scriptTag.dataset.formHostId;

if (!formHostId) { formHostId = 'formhost'; }
formHostSelector = `#${formHostId}`;

fetch(formUrl)
	.then(function (response) { return response.text(); })
	.then(function (body) { document.querySelector(formHostSelector).innerHTML = body; })
	.then(function () {
		var arr = document.querySelector(formHostSelector).getElementsByTagName('script')
		for (var n = 0; n < arr.length; n++)
			eval(arr[n].innerHTML) //run script inside div
	});