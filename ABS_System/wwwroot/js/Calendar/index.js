// wwwroot/js/calendar/index.js

(function () {
  // NOTE:
  // allAppointments, agentNames, customerNames, agentColors are provided
  // by Calendar/Index.cshtml via window.__CALENDAR_BOOTSTRAP__

  function boot() {
    var boot = window.__CALENDAR_BOOTSTRAP__ || {};
    var allAppointments = boot.allAppointments || [];
    window.agentNames = boot.agentNames || {};
    window.customerNames = boot.customerNames || {};
    window.agentColors = boot.agentColors || {};

    function getAntiForgeryToken() {
      var el = document.querySelector('#antiForgeryForm input[name="__RequestVerificationToken"]');
      return el ? el.value : '';
    }

    function escHtml(s) {
      return (s || '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
    }

    function toTitleCase(s) {
      s = (s || '').toString().trim().toLowerCase();
      if (!s) return s;
      return s.replace(/(^|[\s-])([a-z])/g, function (_, p1, p2) { return p1 + p2.toUpperCase(); });
    }

    function isFulfilled(appt) {
      var status = (appt.Status || '').toString().trim().toUpperCase();
      return status === 'FULFILLED' || status === 'DONE' || status === 'COMPLETED';
    }

    function cssEscape(v) {
      if (window.CSS && typeof window.CSS.escape === 'function') return window.CSS.escape(String(v));
      return String(v).replace(/"/g, '\\"');
    }

    // ====== AJAX DELETE CONFIRM ======
    var pendingDeleteId = null;

    function openConfirmDeleteModal(apptId) {
      pendingDeleteId = apptId || null;
      var overlay = document.getElementById('modalOverlay');
      var m = document.getElementById('confirmDeleteModal');
      if (overlay) overlay.style.display = 'block';
      if (m) m.style.display = 'block';
    }

    function closeConfirmDeleteModal() {
      pendingDeleteId = null;
      var m = document.getElementById('confirmDeleteModal');
      if (m) m.style.display = 'none';

      // keep overlay only if day/month modal still open
      var dayOpen = (document.getElementById('dayAppointmentsModal')?.style.display === 'block');
      var monthOpen = (document.getElementById('monthAppointmentsModal')?.style.display === 'block');

      if (!dayOpen && !monthOpen) {
        var overlay = document.getElementById('modalOverlay');
        if (overlay) overlay.style.display = 'none';
      }
    }

    async function deleteAppointmentAjax(apptId) {
      var token = getAntiForgeryToken();

      var body = new URLSearchParams();
      body.append('id', apptId);
      body.append('__RequestVerificationToken', token);

      var res = await fetch('/Appointment/DeleteAjax', {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8' },
        body: body.toString()
      });

      if (!res.ok) throw new Error('HTTP ' + res.status);

      var json = await res.json();
      if (!json || json.ok !== true) {
        throw new Error((json && json.message) ? json.message : 'Delete failed');
      }
      return json;
    }

    function removeAppointmentFromUI(apptId) {
      allAppointments = (allAppointments || []).filter(function (a) {
        return String(a.ApptId) !== String(apptId);
      });

      document.querySelectorAll('.appt-card.modal-card[data-appt-id="' + cssEscape(apptId) + '"]').forEach(function (n) {
        n.remove();
      });

      document.querySelectorAll('.appt-card.in-cell[data-appt-id="' + cssEscape(apptId) + '"]').forEach(function (n) {
        n.remove();
      });

      var dayList = document.getElementById('day-appointments-list');
      if (dayList && dayList.children.length === 0) dayList.innerHTML = '<div class="appt-empty">No appointments.</div>';

      var monthList = document.getElementById('month-appointments-list');
      if (monthList && monthList.children.length === 0) monthList.innerHTML = '<div class="appt-empty">No appointments.</div>';
    }

    function wireDeleteConfirm(containerEl) {
      if (!containerEl) return;

      containerEl.querySelectorAll('a.appt-delete').forEach(function (a) {
        a.addEventListener('click', function (e) {
          e.preventDefault();   // stop navigation
          e.stopPropagation();
          var apptId = a.getAttribute('data-appt-id');
          openConfirmDeleteModal(apptId);
        });
      });
    }

    // ====== status update AJAX ======
    async function setAppointmentStatus(apptId, newStatus) {
      var token = getAntiForgeryToken();

      var body = new URLSearchParams();
      body.append('id', apptId);
      body.append('status', newStatus);
      body.append('__RequestVerificationToken', token);

      var res = await fetch('/Appointment/SetStatus', {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8' },
        body: body.toString()
      });

      if (!res.ok) throw new Error('HTTP ' + res.status);

      var json = await res.json();
      if (!json || json.ok !== true) throw new Error((json && json.message) ? json.message : 'Update failed');

      return json;
    }

    function wireTodoCheckboxes(containerEl) {
      if (!containerEl) return;

      containerEl.querySelectorAll('.appt-done-cb').forEach(function (cb) {
        cb.addEventListener('click', function (e) { e.stopPropagation(); });

        cb.addEventListener('change', async function (e) {
          e.stopPropagation();

          var card = cb.closest('.appt-card');
          if (!card) return;

          var apptId = cb.getAttribute('data-appt-id') || card.getAttribute('data-appt-id');
          var wantDone = cb.checked;
          var newStatus = wantDone ? 'FULFILLED' : 'BOOKED';

          card.classList.toggle('is-fulfilled', wantDone);
          card.classList.add('appt-saving');

          try {
            await setAppointmentStatus(apptId, newStatus);

            var found = (allAppointments || []).find(function (a) { return String(a.ApptId) === String(apptId); });
            if (found) found.Status = newStatus;

          } catch (err) {
            cb.checked = !wantDone;
            card.classList.toggle('is-fulfilled', !wantDone);
            alert('Failed to update status: ' + (err && err.message ? err.message : err));
          } finally {
            card.classList.remove('appt-saving');
          }
        });
      });
    }

    // ====== Build appointment card for modal ======
    function buildApptCard(appt) {
      var when = '';
      var timeOnly = '';
      if (appt.ApptStart) {
        when = appt.ApptStart.replace('T', ' ').substring(0, 16);
        timeOnly = appt.ApptStart.replace('T', ' ').substring(11, 16);
      }

      var cust = (window.customerNames && window.customerNames[appt.CustomerCode])
        ? window.customerNames[appt.CustomerCode]
        : (appt.CustomerCode || '');

      var agent = (window.agentNames && window.agentNames[appt.AgentCode])
        ? window.agentNames[appt.AgentCode]
        : (appt.AgentCode || '');

      var cardColor = (window.agentColors && window.agentColors[appt.AgentCode])
        ? window.agentColors[appt.AgentCode]
        : null;

      var style = cardColor ? ' style="background-color:' + escHtml(cardColor) + ' !important;"' : '';
      var done = isFulfilled(appt);
      var doneClass = done ? ' is-fulfilled' : '';

      return (
        '<div class="appt-card modal-card' + doneClass + '" data-appt-id="' + appt.ApptId + '"' + style + '>' +
          '<div class="appt-top">' +
            '<div class="appt-title">' + escHtml(toTitleCase(appt.Title || '')) + '</div>' +
            '<div class="appt-top-right">' +
              '<div class="appt-time">' + escHtml(timeOnly || '') + '</div>' +
              '<label class="appt-done-wrap" title="Mark as fulfilled">' +
                '<span></span>' +
                // '<input type="checkbox" class="appt-done-cb" data-appt-id="' + appt.ApptId + '" ' + (done ? 'checked' : '') + ' />' +
              '</label>' +
            '</div>' +
          '</div>' +

          '<div class="appt-meta">' +
            '<div class="appt-meta-line"><span class="appt-meta-label">When:</span> <span class="appt-meta-value">' + escHtml(when) + '</span></div>' +
            '<div class="appt-meta-line"><span class="appt-meta-label">Cust:</span> <span class="appt-meta-value">' + escHtml(cust) + '</span></div>' +
            '<div class="appt-meta-line"><span class="appt-meta-label">Agent:</span> <span class="appt-meta-value">' + escHtml(agent) + '</span></div>' +

            '<div class="appt-actions" style="margin-top:6px; display:flex; gap:6px; align-items:center; flex-wrap:wrap;">' +
              '<a href="/Appointment/Edit/' + appt.ApptId + '" class="btn btn-sm btn-primary">Edit</a>' +
              '<a href="/Appointment/Sign/' + appt.ApptId + '" class="btn btn-sm btn-secondary">Submit E-Sign</a>' +
              '<a href="#" class="btn btn-sm btn-danger appt-delete" data-appt-id="' + appt.ApptId + '">Delete</a>' +
            '</div>' +
          '</div>' +
        '</div>'
      );
    }

    // ====== show modals ======
    window.showMonthModal = function showMonthModal(year, month) {
      var modalEl = document.getElementById('monthAppointmentsModal');
      var label = document.getElementById('modalMonthLabel');
      var list = document.getElementById('month-appointments-list');
      var overlay = document.getElementById('modalOverlay');

      var monthStr = year + '-' + (month.toString().length === 1 ? '0' + month : month);
      label.textContent = monthStr;
      list.innerHTML = '';

      var appts = (allAppointments || []).filter(function (a) {
        return a.ApptStart && a.ApptStart.startsWith(monthStr);
      });

      if (appts.length === 0) {
        list.innerHTML = '<div class="appt-empty">No appointments.</div>';
      } else {
        appts.sort(function (a, b) { return (a.ApptStart || '').localeCompare(b.ApptStart || ''); });

        var html = '';
        appts.forEach(function (appt) { html += buildApptCard(appt); });
        list.innerHTML = html;

        wireTodoCheckboxes(list);
        wireDeleteConfirm(list);
      }

      overlay.style.display = 'block';
      modalEl.style.display = 'block';
    };

    function showDayModal(date) {
      var modalEl = document.getElementById('dayAppointmentsModal');
      var label = document.getElementById('modalDayLabel');
      var list = document.getElementById('day-appointments-list');
      var addBtn = document.getElementById('add-appointment-btn');
      var overlay = document.getElementById('modalOverlay');

      try {
        var d = new Date(date);
        if (!isNaN(d.getTime())) {
          label.textContent = d.getDate() + ' ' + d.toLocaleString('default', { month: 'long' }) + ' ' + d.getFullYear();
        } else {
          label.textContent = date;
        }
      } catch (e) {
        label.textContent = date;
      }

      addBtn.href = '/Appointment/Create?apptStart=' + date + 'T09:00';
      list.innerHTML = '';

      var appts = (allAppointments || []).filter(function (a) {
        return a.ApptStart && a.ApptStart.startsWith(date);
      });

      if (appts.length === 0) {
        list.innerHTML = '<div class="appt-empty">No appointments.</div>';
      } else {
        appts.sort(function (a, b) { return (a.ApptStart || '').localeCompare(b.ApptStart || ''); });

        var html = '';
        appts.forEach(function (appt) { html += buildApptCard(appt); });
        list.innerHTML = html;

        wireTodoCheckboxes(list);
        wireDeleteConfirm(list);
      }

      overlay.style.display = 'block';
      modalEl.style.display = 'block';
    }

    // ====== init ======
    document.addEventListener('DOMContentLoaded', function () {

      document.querySelectorAll('.calendar-cell-day').forEach(function (cell) {
        cell.addEventListener('click', function (e) {
          e.stopPropagation();
          showDayModal(cell.getAttribute('data-date'));
        });

        cell.addEventListener('keydown', function (e) {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            showDayModal(cell.getAttribute('data-date'));
          }
        });
      });

      function closeMainModals() {
        document.getElementById('dayAppointmentsModal').style.display = 'none';
        document.getElementById('monthAppointmentsModal').style.display = 'none';
        document.getElementById('modalOverlay').style.display = 'none';
      }

      document.getElementById('closeDayModal').addEventListener('click', closeMainModals);
      document.getElementById('closeMonthModal').addEventListener('click', closeMainModals);
      document.getElementById('modalOverlay').addEventListener('click', closeMainModals);
      document.getElementById('modalOverlay').addEventListener('touchstart', closeMainModals);

      document.querySelectorAll('.custom-modal-content').forEach(function (modalContent) {
        modalContent.addEventListener('mousedown', function (e) { e.stopPropagation(); });
        modalContent.addEventListener('touchstart', function (e) { e.stopPropagation(); });
      });

      // confirm delete modal buttons
      var btnCancel = document.getElementById('btnDeleteCancel');
      var btnYes = document.getElementById('btnDeleteYes');
      var btnCloseX = document.getElementById('closeConfirmDeleteModal');

      if (btnCancel) btnCancel.addEventListener('click', function (e) {
        e.preventDefault();
        closeConfirmDeleteModal();
      });

      if (btnCloseX) btnCloseX.addEventListener('click', function (e) {
        e.preventDefault();
        closeConfirmDeleteModal();
      });

      if (btnYes) btnYes.addEventListener('click', async function (e) {
        e.preventDefault();

        if (!pendingDeleteId) {
          closeConfirmDeleteModal();
          return;
        }

        btnYes.disabled = true;

        try {
          await deleteAppointmentAjax(pendingDeleteId);
          removeAppointmentFromUI(pendingDeleteId);
          closeConfirmDeleteModal();
        } catch (err) {
          alert('Failed to delete appointment: ' + (err && err.message ? err.message : err));
        } finally {
          btnYes.disabled = false;
        }
      });
    });
  }

  boot();
})();
