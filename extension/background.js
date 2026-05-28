'use strict';

// Service worker minimal — WebHID ne peut pas être utilisé ici (pas de geste utilisateur).
// La lecture batterie se fait entièrement dans le popup.

chrome.runtime.onInstalled.addListener(() => {
  console.log('[Razer Battery] Extension installée.');
});
