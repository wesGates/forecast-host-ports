**free
// FCSTNAIVE: the host's fallback forecast and the suggested-order rule.
//
// 1. For every series in SERIES and every origin day T from pFrom to pTo, write the
//    seasonal naive forecast ("this day last week") for T+1..T+7 into FORECAST under
//    method SEASONAL_NAIVE. The forecast for T+h is the sales on T+h-7.
// 2. Fill SUGGESTED for the same origins with the pModel row where FORECAST has one
//    and the SEASONAL_NAIVE row otherwise. SOURCE names the method used.
//
// Both steps replace their rows for the origin range, so a rerun gives the same rows.
// Tables are found through the library list (system naming); the caller puts the
// project library on it. No commitment control: the library has no journal.
// Any error ends the program with escape message CPF9898 to its caller.
//
// Compile, with <lib> on the library list so the precompiler sees the tables:
//          CRTSQLRPGI OBJ(<lib>/FCSTNAIVE) SRCSTMF('.../fcstnaive.sqlrpgle')
//            OBJTYPE(*PGM) COMMIT(*NONE) OPTION(*SYS) CVTCCSID(*JOB)
//          CVTCCSID(*JOB) because the precompiler rejects a UTF-8 source as it is.
// Call:    CALL <lib>/FCSTNAIVE PARM('2015-05-24' '2016-05-15' '<model, 40 bytes>')

ctl-opt dftactgrp(*no) actgrp(*new) option(*srcstmt: *nodebugio);

dcl-pi *n;
  pFrom char(10) const;
  pTo char(10) const;
  pModel char(40) const;
end-pi;

dcl-c VERSION 'FCSTNAIVE 1.0';
dcl-c SEASON 7;
dcl-c HORIZON 7;
dcl-c MAXDAYS 5000;
dcl-c MAXROWS 7000;

// One series' daily sales, oldest first.
dcl-ds sale qualified dim(MAXDAYS);
  d date(*iso);
  units int(10);
end-ds;
dcl-s nSales int(10);

// Forecast rows waiting for a blocked insert, in FORECAST's column order.
dcl-ds fc qualified dim(MAXROWS);
  as_of date(*iso);
  id varchar(40);
  item_id varchar(20);
  store_id varchar(8);
  method varchar(40);
  horizon int(10);
  target_date date(*iso);
  forecast packed(15: 6);
  fallback int(5);
  code_digest varchar(64);
  made_at timestamp;
end-ds;
dcl-s nOut int(10);

dcl-s fromDate date(*iso);
dcl-s toDate date(*iso);
dcl-s model varchar(40);
dcl-s method varchar(40) inz('SEASONAL_NAIVE');
dcl-s madeAt timestamp;
dcl-s sid varchar(40);
dcl-s sItem varchar(20);
dcl-s sStore varchar(8);

exec sql set option commit = *none, naming = *sys, datfmt = *iso,
                    closqlcsr = *endmod;

fromDate = %date(pFrom: *iso);
toDate = %date(pTo: *iso);
model = %trimr(pModel);
if toDate < fromDate;
  fail('origin range ' + pFrom + ' to ' + pTo + ' is empty');
endif;
madeAt = %timestamp();

// Step 1: the seasonal naive forecast for every series.
exec sql delete from FORECAST
          where method = :method and as_of between :fromDate and :toDate;
checkSql('delete from FORECAST');

exec sql declare cSeries cursor for
           select id, item_id, store_id from SERIES order by id;
exec sql open cSeries;
checkSql('open SERIES');
dow '1';
  exec sql fetch next from cSeries into :sid, :sItem, :sStore;
  checkSql('fetch SERIES');
  if sqlcode = 100;
    leave;
  endif;
  loadSales();
  forecastSeries();
enddo;
exec sql close cSeries;
flush();

// Step 2: the model's row where present, the fallback otherwise.
exec sql delete from SUGGESTED where as_of between :fromDate and :toDate;
checkSql('delete from SUGGESTED');

exec sql insert into SUGGESTED
                (as_of, id, item_id, store_id, horizon, target_date, forecast, source)
         select f.as_of, f.id, f.item_id, f.store_id, f.horizon, f.target_date,
                coalesce(m.forecast, f.forecast), coalesce(m.method, f.method)
           from FORECAST f
           left join FORECAST m
             on m.as_of = f.as_of and m.id = f.id
            and m.target_date = f.target_date and m.method = :model
          where f.method = :method and f.as_of between :fromDate and :toDate;
checkSql('insert into SUGGESTED');

*inlr = *on;
return;


// Read the sales of series sid into sale, and check they run day by day.
dcl-proc loadSales;
  dcl-s i int(10);

  exec sql declare cSales cursor for
             select date, units from SALES where id = :sid order by date;
  exec sql open cSales;
  checkSql('open SALES');
  exec sql fetch next from cSales for 5000 rows into :sale;
  checkSql('fetch SALES');
  nSales = sqlerrd(3);
  exec sql close cSales;

  if nSales = 0;
    fail('no sales for ' + sid);
  endif;
  if nSales = MAXDAYS;
    fail('more than ' + %char(MAXDAYS - 1) + ' sales days for ' + sid);
  endif;
  for i = 2 to nSales;
    if sale(i).d <> sale(1).d + %days(i - 1);
      fail('sales for ' + sid + ' skip a day before ' + %char(sale(i).d: *iso));
    endif;
  endfor;
end-proc;


// Seven rows per origin: the forecast for T+h is the sales on T+h-SEASON.
dcl-proc forecastSeries;
  dcl-s asOf date(*iso);
  dcl-s t int(10);
  dcl-s h int(10);

  if fromDate - %days(SEASON - 1) < sale(1).d or toDate > sale(nSales).d;
    fail('sales for ' + sid + ' do not cover the origins');
  endif;

  asOf = fromDate;
  dow asOf <= toDate;
    t = %diff(asOf: sale(1).d: *days) + 1;   // index of the origin day T
    for h = 1 to HORIZON;
      addRow(asOf: h: sale(t + h - SEASON).units);
    endfor;
    asOf += %days(1);
  enddo;
end-proc;


dcl-proc addRow;
  dcl-pi *n;
    asOf date(*iso) const;
    h int(10) const;
    units int(10) const;
  end-pi;

  if nOut = MAXROWS;
    flush();
  endif;
  nOut += 1;
  fc(nOut).as_of = asOf;
  fc(nOut).id = sid;
  fc(nOut).item_id = sItem;
  fc(nOut).store_id = sStore;
  fc(nOut).method = method;
  fc(nOut).horizon = h;
  fc(nOut).target_date = asOf + %days(h);
  fc(nOut).forecast = units;
  fc(nOut).fallback = 0;
  fc(nOut).code_digest = VERSION;
  fc(nOut).made_at = madeAt;
end-proc;


// Blocked insert of the waiting rows.
dcl-proc flush;
  if nOut > 0;
    exec sql insert into FORECAST :nOut rows values(:fc);
    checkSql('insert into FORECAST');
    nOut = 0;
  endif;
end-proc;


dcl-proc checkSql;
  dcl-pi *n;
    what varchar(60) const;
  end-pi;

  if sqlcode < 0;
    fail(what + ' failed with SQLCODE ' + %char(sqlcode));
  endif;
end-proc;


// End the program with escape message CPF9898 to the program's caller.
dcl-proc fail;
  dcl-pi *n;
    text varchar(200) const;
  end-pi;

  dcl-pr sendProgramMessage extpgm('QMHSNDPM');
    msgId char(7) const;
    msgFile char(20) const;
    msgData char(256) const;
    msgDataLen int(10) const;
    msgType char(10) const;
    stackEntry char(10) const;
    stackCounter int(10) const;
    msgKey char(4);
    errorCode char(8);
  end-pr;

  dcl-s msg varchar(256);
  dcl-s key char(4);
  dcl-s errorCode char(8) inz(*allx'00');   // bytes provided 0: errors are signalled

  msg = 'FCSTNAIVE: ' + text;
  sendProgramMessage('CPF9898': 'QCPFMSG   *LIBL': msg: %len(msg): '*ESCAPE':
                     '*PGMBDY': 1: key: errorCode);
end-proc;
